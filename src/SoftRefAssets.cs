using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SoftReferenceableAssets;
using UnityEngine;

namespace Ezomic.Core
{
    /// <summary>
    /// Lets a runtime-built prefab be referenced by <see cref="SoftReference{T}"/>.
    ///
    /// Since the soft-reference rework, the parts of the game worth extending no longer hold
    /// a plain GameObject. <c>ZoneSystem.ZoneLocation.m_prefab</c> and
    /// <c>DungeonDB.RoomData.m_prefab</c> are both <c>SoftReference&lt;GameObject&gt;</c>, and
    /// every one of them resolves through the private static loader behind
    /// <c>SoftReferenceableAssets.Runtime.Loader</c>. There is no public way to hand that
    /// loader an object that did not come out of a bundle, so a mod with no asset bundle
    /// cannot register a location or a room at all.
    ///
    /// The way through is to wrap the loader. <see cref="IAssetLoader"/> is public, so a shim
    /// can implement it, answer for its own ids, and pass everything else straight to the real
    /// loader. Seventeen methods, sixteen of them one line.
    ///
    /// This is deliberately in Core rather than in the mod that needed it first, because
    /// anything that wants to add a location, a room, a scene-referenced prefab or a dungeon
    /// needs exactly this and nothing else.
    /// </summary>
    public static class SoftRefAssets
    {
        /// <summary>
        /// Path prefix for everything registered here. It never touches the filesystem - it
        /// exists because <c>SoftReference.Name</c> is <c>Shared.GetFileName(path)</c>, so the
        /// path is what gives an asset the name the game will hash it by.
        /// </summary>
        public const string PathRoot = "Assets/Ezomic/";

        private static Shim _shim;

        /// <summary>
        /// Register a prefab and get a reference the game will accept.
        ///
        /// <paramref name="name"/> becomes the asset's name, which is what
        /// <c>ZoneLocation.Hash</c> and <c>RoomData.Hash</c> are computed from and what saved
        /// ZDOs will store. It is permanent from the first world that uses it - see the note
        /// on renaming in any of the dungeon READMEs.
        /// </summary>
        public static SoftReference<GameObject> Register(string category, string name, GameObject prefab)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            Install();
            return new SoftReference<GameObject>(_shim.Add(PathRoot + category + "/" + name + ".prefab", prefab));
        }

        /// <summary>
        /// Put the shim in place, once.
        ///
        /// Called lazily rather than from a plugin's Awake on purpose. Reading
        /// <c>Runtime.Loader</c> constructs the real loader if it does not exist yet, and
        /// <c>Runtime.MakeAllAssetsLoadable</c> and <c>Runtime.AddManifest</c> both log an
        /// error and do nothing once it does. Forcing it early would silently break whatever
        /// the game was going to configure. Every caller here runs at ZoneSystem or DungeonDB
        /// setup time, which is long after the game has loaded its own bundles, so by then the
        /// loader always exists and wrapping it is free.
        /// </summary>
        private static void Install()
        {
            if (_shim != null) return;

            FieldInfo field = typeof(Runtime).GetField(
                "s_assetLoader", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                throw new InvalidOperationException(
                    "SoftReferenceableAssets.Runtime.s_assetLoader is gone. The asset loader "
                    + "changed shape in a game update and SoftRefAssets needs rewriting.");

            IAssetLoader inner = field.GetValue(null) as IAssetLoader;
            if (inner == null)
            {
                // Should not happen this late in startup. Force it rather than fail, but say
                // so, because it means the ordering assumption above no longer holds.
                CorePlugin.Log.LogWarning(
                    "Asset loader did not exist yet; forcing it. If asset loading misbehaves "
                    + "from here, this line is why.");
                PropertyInfo prop = typeof(Runtime).GetProperty(
                    "Loader", BindingFlags.NonPublic | BindingFlags.Static);
                inner = (IAssetLoader)prop.GetValue(null, null);
            }

            // Already wrapped - a reload, or two mods in this family racing. Reuse it, or the
            // second shim would hide the first one's assets behind a delegate chain.
            _shim = inner as Shim;
            if (_shim != null) return;

            _shim = new Shim(inner);
            field.SetValue(null, _shim);
            CorePlugin.Log.LogInfo("Asset loader wrapped; runtime prefabs can be soft-referenced.");
        }

        /// <summary>
        /// Delegates everything it does not own. The only interesting parts are
        /// <see cref="Add"/> and the four lookups that must answer before the real loader sees
        /// an id it has never heard of - <c>AssetBundleLoader</c> indexes a dictionary
        /// unguarded, so an unknown id is a KeyNotFoundException rather than a null.
        /// </summary>
        private sealed class Shim : IAssetLoader
        {
            private readonly IAssetLoader _inner;
            private readonly Dictionary<AssetID, UnityEngine.Object> _assets =
                new Dictionary<AssetID, UnityEngine.Object>();
            private readonly Dictionary<AssetID, string> _paths = new Dictionary<AssetID, string>();
            private readonly Dictionary<string, AssetID> _byPath =
                new Dictionary<string, AssetID>(StringComparer.Ordinal);

            internal Shim(IAssetLoader inner) { _inner = inner; }

            internal AssetID Add(string path, UnityEngine.Object asset)
            {
                AssetID existing;
                if (_byPath.TryGetValue(path, out existing))
                {
                    // Re-registering the same path is a plugin reload, not a mistake. Keep the
                    // id so anything already referencing it stays valid, and take the new
                    // object.
                    _assets[existing] = asset;
                    return existing;
                }

                AssetID id = IdFor(path);
                if (_inner.IsAvailable(id))
                    throw new InvalidOperationException(
                        "Asset id for '" + path + "' collides with a real game asset. "
                        + "Rename it - a 128-bit collision means something is wrong, not unlucky.");

                _assets[id] = asset;
                _paths[id] = path;
                _byPath[path] = id;
                return id;
            }

            /// <summary>
            /// A stable 128-bit id derived from the path.
            ///
            /// Stable matters: ids end up compared and logged, and one that changed per run
            /// would make two sessions disagree about the same asset for no visible reason.
            /// MD5 is used as a hash function and nothing else - there is nothing to attack
            /// here, and it is the only 128-bit digest in the framework that needs no setup.
            /// </summary>
            private static AssetID IdFor(string path)
            {
                using (MD5 md5 = MD5.Create())
                {
                    byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
                    return new AssetID(
                        BitConverter.ToUInt32(h, 0),
                        BitConverter.ToUInt32(h, 4),
                        BitConverter.ToUInt32(h, 8),
                        BitConverter.ToUInt32(h, 12));
                }
            }

            private bool Owns(AssetID id) { return _assets.ContainsKey(id); }

            // --- the four that must answer first ------------------------------------------

            public T Get<T>(AssetID assetID) where T : UnityEngine.Object
            {
                UnityEngine.Object asset;
                if (_assets.TryGetValue(assetID, out asset)) return asset as T;
                return _inner.Get<T>(assetID);
            }

            public bool IsAvailable(AssetID assetID)
            {
                return Owns(assetID) || _inner.IsAvailable(assetID);
            }

            public bool IsLoaded(AssetID assetID)
            {
                // Ours are in memory from the moment they are registered; there is nothing to
                // load and never will be.
                return Owns(assetID) || _inner.IsLoaded(assetID);
            }

            public string GetPath(AssetID assetID)
            {
                string path;
                if (_paths.TryGetValue(assetID, out path)) return path;
                return _inner.GetPath(assetID);
            }

            // --- loading, which for our assets is already done ----------------------------

            public LoadResult Load(AssetID assetID)
            {
                return Owns(assetID) ? LoadResult.Succeeded : _inner.Load(assetID);
            }

            public void LoadAsync(AssetID assetID)
            {
                if (!Owns(assetID)) _inner.LoadAsync(assetID);
            }

            public void CallbackWhenLoaded(AssetID assetID, LoadedHandler callback)
            {
                // Straight through, not deferred a frame. The caller that matters here is
                // ZoneSystem's location preloader, which counts rooms as they arrive; making
                // it wait for something that is already in memory would only add a frame where
                // the count is wrong.
                if (Owns(assetID)) callback(assetID, LoadResult.Succeeded);
                else _inner.CallbackWhenLoaded(assetID, callback);
            }

            public void WaitForLoadToComplete(AssetID assetID)
            {
                if (!Owns(assetID)) _inner.WaitForLoadToComplete(assetID);
            }

            public bool IsLoading(AssetID assetID)
            {
                return Owns(assetID) ? false : _inner.IsLoading(assetID);
            }

            // --- reference counting, which ours opt out of --------------------------------

            public void IncrementReferenceCounter(AssetID assetID)
            {
                // Ours are held by the plugin for the process lifetime. Counting references to
                // something that can never be unloaded would only give the game a number it
                // could act on.
                if (!Owns(assetID)) _inner.IncrementReferenceCounter(assetID);
            }

            public void Release(AssetID assetID)
            {
                if (!Owns(assetID)) _inner.Release(assetID);
            }

            // --- swap-loading: split the batch, because ours need no slot ------------------

            public LoadResult SwapLoad(AssetID assetIDToLoad, AssetID assetIDToUnload)
            {
                return SwapLoad(new[] { assetIDToLoad }, new[] { assetIDToUnload });
            }

            public LoadResult SwapLoad(AssetID assetIDToLoad, AssetID[] assetIDsToUnload)
            {
                return SwapLoad(new[] { assetIDToLoad }, assetIDsToUnload);
            }

            public LoadResult SwapLoad(AssetID[] assetsIDToLoad, AssetID[] assetIDsToUnload)
            {
                List<AssetID> load = Foreign(assetsIDToLoad);
                List<AssetID> unload = Foreign(assetIDsToUnload);

                // Everything in the batch was ours, so there is nothing to swap and nothing to
                // report but success. Passing two empty arrays down would make the real loader
                // do a pointless pass over its bundles.
                if (load.Count == 0 && unload.Count == 0) return LoadResult.Succeeded;

                return _inner.SwapLoad(load.ToArray(), unload.ToArray());
            }

            private List<AssetID> Foreign(AssetID[] ids)
            {
                List<AssetID> result = new List<AssetID>(ids.Length);
                for (int i = 0; i < ids.Length; i++)
                    if (!Owns(ids[i])) result.Add(ids[i]);
                return result;
            }

            // --- scenes: never ours -------------------------------------------------------

            public void LoadScene(AssetID assetID, SoftReferenceableAssets.SceneManagement.LoadSceneMode mode)
            {
                _inner.LoadScene(assetID, mode);
            }

            public void LoadScene(string sceneName, SoftReferenceableAssets.SceneManagement.LoadSceneMode mode)
            {
                _inner.LoadScene(sceneName, mode);
            }

            public SoftReferenceableAssets.SceneManagement.ILoadSceneAsyncOperation LoadSceneAsync(
                AssetID assetID, SoftReferenceableAssets.SceneManagement.LoadSceneMode mode)
            {
                return _inner.LoadSceneAsync(assetID, mode);
            }

            public SoftReferenceableAssets.SceneManagement.ILoadSceneAsyncOperation LoadSceneAsync(
                string sceneName, SoftReferenceableAssets.SceneManagement.LoadSceneMode mode)
            {
                return _inner.LoadSceneAsync(sceneName, mode);
            }

            public Dictionary<string, AssetID> GetAllAssetPathsMappedToAssetID()
            {
                Dictionary<string, AssetID> all = _inner.GetAllAssetPathsMappedToAssetID();
                foreach (KeyValuePair<string, AssetID> pair in _byPath) all[pair.Key] = pair.Value;
                return all;
            }
        }
    }
}
