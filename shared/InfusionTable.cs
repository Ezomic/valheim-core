using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Ezomic.Shared
{
    /// <summary>What fills the Infusion Table's basin, and what a recipe can ask for.</summary>
    public enum InfusionLiquid
    {
        /// <summary>For a recipe: any of the three. Never what a basin holds.</summary>
        Any,
        Resin,
        GreydwarfEye,
        Guck
    }

    /// <summary>
    /// The Infusion Table: one crafting station shared by every mod that wants to combine two
    /// items into a third, and a helper for adding recipes that use it.
    ///
    /// <b>This file is shared source, not a library</b>, for the reason <see cref="Prefabs"/>
    /// gives: Core is a soft dependency, and a mod that cannot register its own station must
    /// not need Core to do so. It is linked into each consumer and is not part of
    /// EzomicCore.dll. It also needs <c>shared\Prefabs.cs</c> linked, and uses it for the
    /// parts that are not specific to a table: the holder, the clone, the ZNetScene
    /// registration and the tool's piece table.
    ///
    /// <code>
    /// &lt;Compile Include="..\core\shared\Prefabs.cs" Link="shared\Prefabs.cs" /&gt;
    /// &lt;Compile Include="..\core\shared\InfusionTable.cs" Link="shared\InfusionTable.cs" /&gt;
    /// </code>
    ///
    /// <b>The model and icon travel beside the DLL.</b> There are no asset bundles in this
    /// suite, so the mesh is an .obj read at runtime and the icon a .png, both looked up
    /// next to the linking mod's own assembly. The csproj of every consumer copies them in
    /// its Deploy target, next to the line that copies the DLL:
    ///
    /// <code>
    /// &lt;Copy SourceFiles="..\core\shared\assets\infusion_table.obj;..\core\shared\assets\infusion_table.png"
    ///       DestinationFolder="$(DeployDir)" ContinueOnError="true" /&gt;
    /// </code>
    ///
    /// and its Thunderstore package carries the same two files. A consumer that forgets
    /// still gets a working table: it logs once and wears the workbench's own look.
    ///
    /// <code>
    /// // once, in Awake
    /// Prefabs.Log = Logger;
    /// InfusionTable.AddRecipe("SwordIron", "Ruby", "SwordIronInfused");
    ///
    /// // every frame, in Update
    /// InfusionTable.Tick();
    /// </code>
    ///
    /// <b>Prefab name <c>piece_ezomic_infusion</c> is permanent.</b> It is the network
    /// identity ZNetScene keys on and every placed table stores its hash, so renaming it, or
    /// failing to register it in a world that has one placed, discards every table in that
    /// world silently. What is stored and compared is that name. What a player sees is
    /// "Infusion Table".
    ///
    /// <b>Per world, and across mods.</b> Registration asks the live ZNetScene whether it
    /// already knows the prefab and registers only if it does not, so two mods linking this
    /// file never double-register: whichever ticks first wins the world and the other finds
    /// the prefab there. The Hammer's piece table and ObjectDB's recipe list get the same
    /// treatment, asked rather than remembered, because both are rebuilt with every world.
    /// The only state kept for the process is the built prefab, which is meshes read off
    /// disk and materials, none of which change between worlds. Each linking DLL has its own
    /// copy of these statics, which is exactly why nothing here is an API between mods.
    /// </summary>
    public static class InfusionTable
    {
        /// <summary>The network identity. Never rename it.</summary>
        public const string PrefabName = "piece_ezomic_infusion";

        /// <summary>
        /// What a player sees, and also the CraftingStation's name, which is what the game
        /// matches a recipe's station on. Distinct from the workbench's on purpose: a table
        /// that shared its name would count as a workbench for every recipe that needs one.
        /// </summary>
        public const string DisplayName = "Infusion Table";

        private const string DonorPrefab = "piece_workbench";
        private const string ModelFile = "infusion_table.obj";
        private const string IconFile = "infusion_table.png";

        // Early materials, buildable from the workbench area in the Meadows: Flint rather
        // than Resin, because Resin comes off Greydwarves and would put the table behind the
        // first forest.
        private static readonly string[] CostItems = { "Wood", "Stone", "Flint" };
        private static readonly int[] CostAmounts = { 10, 8, 4 };

        /// <summary>Vanilla things whose material reads as lit, best first.</summary>
        private static readonly string[] GlowDonors = { "guard_stone", "piece_dvergr_lantern" };

        private const string LiquidKey = "ezomic_infusion_liquid";
        private const string HarmonyId = "ezomic.shared.infusiontable";

        private static readonly Color LiquidColour = new Color(0.35f, 0.85f, 1f);
        private static readonly Color DryColour = new Color(0.14f, 0.12f, 0.1f);
        private const float LightRange = 3f;
        private const float LightIntensity = 1.2f;

        private static Color ColourOf(InfusionLiquid liquid)
        {
            switch (liquid)
            {
                case InfusionLiquid.Resin: return new Color(1f, 0.8f, 0.15f);
                case InfusionLiquid.GreydwarfEye: return new Color(0.3f, 0.5f, 1f);
                case InfusionLiquid.Guck: return new Color(0.4f, 1f, 0.3f);
                default: return LiquidColour;
            }
        }

        private static readonly InfusionLiquid[] Fillings =
            { InfusionLiquid.Resin, InfusionLiquid.GreydwarfEye, InfusionLiquid.Guck };

        private static InfusionLiquid? LiquidOfItem(ItemDrop.ItemData item)
        {
            string name = item != null && item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            foreach (InfusionLiquid liquid in Fillings)
                if (name == liquid.ToString()) return liquid;

            return null;
        }

        /// <summary>Seconds between the checks that are not about ZDO safety.</summary>
        private const float HousekeepingInterval = 1f;

        /// <summary>
        /// How many builds may fail before the table is left alone for the rest of the
        /// session. Generous and spaced out: a failed build is usually something the scene
        /// has not finished loading, and giving up on it too early means placed tables go
        /// missing from the world.
        /// </summary>
        private const int MaxFailures = 30;

        private sealed class Want
        {
            internal string Base, Infusing, Output;
            internal InfusionLiquid Liquid;
        }

        private static readonly List<Want> Wanted = new List<Want>();
        private static readonly HashSet<string> Told = new HashSet<string>();

        private static GameObject _prefab;
        private static int _failures;
        private static bool _abandoned;
        private static float _retryAt;
        private static float _nextHousekeeping;
        private static Sprite _icon;
        private static bool _patched;

        // What the prefab was dressed with, so a later world can tell it needs dressing again.
        private static MeshRenderer _renderer;
        private static MeshRenderer _liquidRenderer;
        private static Mesh _mesh, _liquidMesh;
        private static Vector2[] _baseUv, _liquidBaseUv;
        private static string[] _groups;
        private static GameObject _woodSource, _stoneSource, _glowSource;
        private static Material _woodMaterial, _stoneMaterial, _liquidMaterial, _liquidTinted;

        // ------------------------------------------------------------------ public

        /// <summary>
        /// Declare a recipe: <paramref name="baseItem"/> and <paramref name="infusingItem"/>
        /// go in, a fresh <paramref name="outputItem"/> comes out, at the Infusion Table.
        /// Item arguments are prefab names.
        ///
        /// The recipe is added to ObjectDB by <see cref="Tick"/> and added again to every
        /// world after the first. Declaring the same recipe twice is harmless, and so is
        /// declaring one a second mod already declared: an equivalent recipe already in
        /// ObjectDB is left alone.
        ///
        /// A null name is logged and ignored. A name that no item answers to is logged once
        /// and retried, because the item may belong to a mod that registers it a few frames
        /// later.
        ///
        /// Quality is ignored: both inputs are consumed whatever quality they are, which is
        /// why the base item must be one that can only ever be quality 1. A base that can be
        /// upgraded is refused, since vanilla would take a quality 3 sword as readily as a
        /// quality 1 one and nothing here carries quality over.
        ///
        /// The basin must be filled for the recipe to be craftable, and each craft empties it.
        /// <paramref name="liquid"/> says with what: the default accepts any of the three,
        /// a named one only that.
        /// </summary>
        public static void AddRecipe(string baseItem, string infusingItem, string outputItem,
            InfusionLiquid liquid = InfusionLiquid.Any)
        {
            if (string.IsNullOrEmpty(baseItem) || string.IsNullOrEmpty(infusingItem)
                || string.IsNullOrEmpty(outputItem))
            {
                Prefabs.Log.LogWarning("InfusionTable.AddRecipe needs three item names, got '"
                    + baseItem + "', '" + infusingItem + "', '" + outputItem
                    + "'. Recipe ignored.");
                return;
            }

            foreach (Want existing in Wanted)
                if (existing.Base == baseItem && existing.Infusing == infusingItem
                    && existing.Output == outputItem && existing.Liquid == liquid) return;

            Wanted.Add(new Want
            {
                Base = baseItem, Infusing = infusingItem, Output = outputItem, Liquid = liquid
            });
        }

        /// <summary>
        /// Call this from the plugin's Update, next to <c>Prefabs.Tick()</c>. Cheap once
        /// everything is in place: registration is a dictionary read, and the rest runs
        /// about once a second.
        /// </summary>
        public static void Tick()
        {
            ZNetScene scene = ZNetScene.instance;
            ObjectDB db = ObjectDB.instance;
            if (scene == null || !HasItems(db)) return;

            if (!_patched) Patch();

            // Every frame and never throttled: this is the call that stops ZNetScene from
            // discarding a placed table, and the sooner it lands the fewer ZDOs are asked
            // about a prefab the scene has never heard of.
            GameObject table = scene.GetPrefab(PrefabName);
            if (table == null) table = Introduce(scene, db);
            if (table == null) return;

            if (table == _prefab) Reskin(scene, db);

            if (Time.unscaledTime < _nextHousekeeping) return;
            _nextHousekeeping = Time.unscaledTime + HousekeepingInterval;

            AddToHammer(table);
            PlaceRecipes(db, table);
        }

        // ------------------------------------------------------------------ registering

        /// <summary>
        /// ObjectDB's first Awake of a session is a stub with no items. Building against it
        /// finds no Wood, no Stone and no Hammer, so an empty item list means "not yet".
        /// </summary>
        private static bool HasItems(ObjectDB db)
        {
            return db != null && db.m_items != null && db.m_items.Count > 0;
        }

        private static GameObject Introduce(ZNetScene scene, ObjectDB db)
        {
            if (_abandoned || Time.unscaledTime < _retryAt) return null;
            if (_prefab == null && !TryBuild(scene, db)) return null;

            return Prefabs.Register(_prefab) ? _prefab : null;
        }

        private static bool TryBuild(ZNetScene scene, ObjectDB db)
        {
            try
            {
                _prefab = Build(scene, db);
            }
            catch (Exception e)
            {
                Prefabs.Log.LogWarning("Building " + PrefabName + " failed: " + e);
            }

            if (_prefab != null)
            {
                _failures = 0;
                return true;
            }

            _retryAt = Time.unscaledTime + 1f;
            if (++_failures < MaxFailures) return false;

            _abandoned = true;
            Prefabs.Log.LogError(PrefabName + " could not be built after " + MaxFailures
                + " attempts and will not be retried. Tables already placed in a world are "
                + "untouched on disk, but the world will discard them while this stays unbuilt.");
            return false;
        }

        private static void AddToHammer(GameObject table)
        {
            PieceTable pieces = Prefabs.ToolPieces("Hammer");
            if (pieces == null) return;

            // By name, not by reference: another mod's copy of this file may have registered
            // its own build of the same prefab, and the table in the scene is the only one
            // the menu should hold.
            foreach (GameObject piece in pieces.m_pieces)
                if (piece != null && piece.name == PrefabName) return;

            pieces.m_pieces.Add(table);
            Prefabs.Log.LogInfo(PrefabName + " added to the Hammer.");
        }

        // ------------------------------------------------------------------ building

        private static GameObject Build(ZNetScene scene, ObjectDB db)
        {
            GameObject workbench = scene.GetPrefab(DonorPrefab);
            if (workbench == null)
            {
                Once("donor", "No " + DonorPrefab + " in the scene to build the Infusion Table from.");
                return null;
            }

            CraftingStation workbenchStation = workbench.GetComponent<CraftingStation>();
            Piece.Requirement[] cost = Cost(db);
            if (workbenchStation == null || cost == null) return null;

            GameObject clone = Prefabs.Clone(workbench, PrefabName);
            try
            {
                Dress(clone, workbenchStation, cost);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(clone);
                throw;
            }

            return clone;
        }

        private static Piece.Requirement[] Cost(ObjectDB db)
        {
            Piece.Requirement[] cost = new Piece.Requirement[CostItems.Length];
            for (int i = 0; i < cost.Length; i++)
            {
                ItemDrop item = ItemOf(db, CostItems[i]);
                if (item == null)
                {
                    Once("cost:" + CostItems[i], "Cannot cost the Infusion Table: no item named "
                        + CostItems[i] + ".");
                    return null;
                }

                cost[i] = new Piece.Requirement { m_resItem = item, m_amount = CostAmounts[i] };
            }

            return cost;
        }

        private static void Dress(GameObject clone, CraftingStation workbenchStation,
            Piece.Requirement[] cost)
        {
            Piece piece = clone.GetComponent<Piece>();
            CraftingStation station = clone.GetComponent<CraftingStation>();
            if (piece == null || station == null)
                throw new InvalidOperationException(DonorPrefab + " has no Piece or CraftingStation.");

            Sprite icon = LoadIcon();

            piece.m_name = DisplayName;
            piece.m_description = "A basin on a workbench frame, where one item is infused with another.";
            piece.m_resources = cost;

            // The table is a bench of its own, but it is built from the workbench's area: it
            // needs one standing nearby, which is what keeps it an early-game piece.
            piece.m_craftingStation = workbenchStation;
            if (icon != null) piece.m_icon = icon;

            station.m_name = DisplayName;
            if (icon != null) station.m_icon = icon;

            // The workbench lists every recipe that needs no station. The table is for
            // infusing and nothing else.
            station.m_showBasicRecipies = false;

            Reface(clone);
        }

        /// <summary>
        /// Swap the workbench's visuals and collision for the table's own, and leave the
        /// machinery alone: ZNetView, Piece, WearNTear and CraftingStation stay, and so do
        /// the transforms the station points at (roof check, connection point).
        /// </summary>
        private static void Reface(GameObject clone)
        {
            string path = AssetPath(ModelFile);
            Model model = File.Exists(path) ? LoadModel(path) : null;
            if (model == null)
            {
                Once("model", ModelFile + " is missing or unreadable beside the dll (" + path
                    + "). The Infusion Table will look like a workbench until it is copied there.");
                return;
            }

            // Components, not their GameObjects: a renderer's object may also carry the roof
            // check point or the connection point the CraftingStation holds a reference to.
            // Renderers first, because a MeshFilter cannot be destroyed under one.
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                    UnityEngine.Object.DestroyImmediate(renderer);

            foreach (MeshFilter filter in clone.GetComponentsInChildren<MeshFilter>(true))
                UnityEngine.Object.DestroyImmediate(filter);

            foreach (LODGroup group in clone.GetComponentsInChildren<LODGroup>(true))
                UnityEngine.Object.DestroyImmediate(group);

            // Solid colliders only. A trigger is part of how the station works, not part of
            // how it looks.
            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
                if (!collider.isTrigger) UnityEngine.Object.DestroyImmediate(collider);

            GameObject visual = new GameObject("infusion_visual");
            visual.transform.SetParent(clone.transform, false);
            visual.layer = clone.layer;
            visual.AddComponent<MeshFilter>().sharedMesh = model.Mesh;

            _renderer = visual.AddComponent<MeshRenderer>();
            _renderer.sharedMaterials = new Material[model.Groups.Length];
            _mesh = model.Mesh;
            _baseUv = model.Mesh.uv;
            _groups = model.Groups;
            _liquidMesh = model.Liquid;
            _liquidBaseUv = model.Liquid != null ? model.Liquid.uv : null;
            _liquidRenderer = null;
            _woodSource = _stoneSource = _glowSource = null;
            _woodMaterial = _stoneMaterial = _liquidMaterial = null;

            GameObject collision = new GameObject("infusion_collision");
            collision.transform.SetParent(clone.transform, false);
            collision.layer = clone.layer;
            BoxCollider box = collision.AddComponent<BoxCollider>();
            box.center = model.Mesh.bounds.center;
            box.size = model.Mesh.bounds.size;

            // Hover and use land on this collider, so the basin component goes with it: it is
            // the table's Interactable, and hands a plain interact on to the CraftingStation.
            collision.AddComponent<InfusionBasin>();

            GameObject liquid = null;
            if (model.Liquid != null)
            {
                liquid = new GameObject("infusion_liquid");
                liquid.transform.SetParent(clone.transform, false);
                liquid.layer = clone.layer;
                liquid.AddComponent<MeshFilter>().sharedMesh = model.Liquid;
                _liquidRenderer = liquid.AddComponent<MeshRenderer>();
                _liquidRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // WearNTear switches between its three look objects as health falls and does not
            // check that they exist. All three pointing at one object keeps it from ever
            // dereferencing one that is gone, and the table has no worn or broken state.
            WearNTear wear = clone.GetComponent<WearNTear>();
            if (wear != null) wear.m_new = wear.m_worn = wear.m_broken = visual;

            if (liquid != null) AttachLight(liquid.transform, model.Liquid);
        }

        /// <summary>
        /// A point light at the basin, a child of the liquid so that hiding the liquid hides
        /// the light too. Vanilla paints its emission into a texture, so a plain colour on a
        /// borrowed material will not glow, and writing to a shared material would recolour
        /// everything else that wears it. The light is what lights the room.
        /// </summary>
        private static void AttachLight(Transform liquid, Mesh mesh)
        {
            GameObject anchor = new GameObject("infusion_light");
            anchor.transform.SetParent(liquid, false);
            anchor.transform.localPosition = mesh.bounds.center + Vector3.up * 0.1f;

            Light light = anchor.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = LiquidColour;
            light.range = LightRange;
            light.intensity = LightIntensity;
            light.shadows = LightShadows.None;
        }

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Dress the table in the current world's materials, when the ones it wears are not
        /// them. Asked every frame of the world rather than hooked on ObjectDB.Awake and
        /// CopyOtherDB: the prefab a material was borrowed from is a different object after
        /// either, so comparing it is the same test with no patch to apply once per linking
        /// mod.
        /// </summary>
        private static void Reskin(ZNetScene scene, ObjectDB db)
        {
            if (_renderer == null || _mesh == null) return;

            GameObject wood = db.GetItemPrefab("Wood");
            GameObject stone = db.GetItemPrefab("Stone");
            GameObject glow = FirstPrefab(scene, GlowDonors);

            if (_woodMaterial != null && _stoneMaterial != null && _liquidMaterial != null
                && _woodSource == wood && _stoneSource == stone && _glowSource == glow) return;

            GameObject workbench = scene.GetPrefab(DonorPrefab);
            Surface woodSurface = Borrow(false, wood, workbench);
            Surface stoneSurface = Borrow(false, stone, scene.GetPrefab("stone_wall_2x1"), workbench);
            Surface liquidSurface = Borrow(true, glow);

            // No emissive donor: the basin wears stone and the tint and the light carry it.
            if (liquidSurface == null) liquidSurface = stoneSurface;

            if (woodSurface == null || stoneSurface == null)
            {
                Once("surface", "No material to dress the Infusion Table with. It stays unskinned.");
                return;
            }

            Material[] materials = new Material[_groups.Length];
            Rect[] rects = new Rect[_groups.Length];
            for (int i = 0; i < _groups.Length; i++)
            {
                Surface surface = SurfaceFor(_groups[i], woodSurface, stoneSurface);
                materials[i] = surface.Material;
                rects[i] = surface.Rect;
            }

            _renderer.sharedMaterials = materials;
            Remap(_mesh, _baseUv, rects);

            if (_liquidMesh != null && _liquidRenderer != null)
            {
                // A material of our own, copied from the donor, which is never written. Each
                // placed table copies this again and tints that, so two tables can differ.
                if (_liquidTinted != null) UnityEngine.Object.Destroy(_liquidTinted);
                _liquidTinted = new Material(liquidSurface.Material) { name = "infusion_liquid" };
                _liquidRenderer.sharedMaterial = _liquidTinted;
                Remap(_liquidMesh, _liquidBaseUv, new[] { liquidSurface.Rect });
            }

            _woodSource = wood;
            _stoneSource = stone;
            _glowSource = glow;
            _woodMaterial = woodSurface.Material;
            _stoneMaterial = stoneSurface.Material;
            _liquidMaterial = liquidSurface.Material;

            Prefabs.Log.LogInfo("Infusion Table skinned: wood from " + woodSurface.Source
                + ", stone from " + stoneSurface.Source + ", liquid from " + liquidSurface.Source
                + (liquidSurface == stoneSurface ? " (no emissive donor)" : "") + ".");
        }

        private sealed class Surface
        {
            internal Material Material;
            internal Rect Rect;
            internal string Source;
        }

        private static bool IsLiquid(string group)
        {
            return string.Equals(group, "liquid", StringComparison.OrdinalIgnoreCase);
        }

        private static Surface SurfaceFor(string group, Surface wood, Surface stone)
        {
            return string.Equals(group, "stone", StringComparison.OrdinalIgnoreCase) ? stone : wood;
        }

        private static GameObject FirstPrefab(ZNetScene scene, string[] names)
        {
            foreach (string name in names)
            {
                GameObject prefab = scene.GetPrefab(name);
                if (prefab != null) return prefab;
            }

            return null;
        }

        /// <summary>
        /// The first donor, in order, that has a material with an albedo to borrow (and an
        /// _EmissionColor, when asked). Borrowed whole, never with its _MainTex swapped: the
        /// donor's normal map would come with it and be sampled through our UVs.
        /// </summary>
        private static Surface Borrow(bool needEmission, params GameObject[] donors)
        {
            foreach (GameObject donor in donors)
            {
                if (donor == null) continue;

                foreach (MeshRenderer renderer in donor.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material material = renderer.sharedMaterial;
                    if (material == null || material.shader == null) continue;
                    if (!material.HasProperty("_MainTex") || material.GetTexture("_MainTex") == null)
                        continue;
                    if (needEmission && !material.HasProperty("_EmissionColor")) continue;

                    return new Surface
                    {
                        Material = material,
                        Rect = UvRegion(renderer),
                        Source = donor.name + "/" + material.name
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// The slice of its texture a donor actually uses, measured from its largest single
        /// triangle. Valheim's textures are atlases, so UVs running 0..1 would sample the
        /// neighbouring tiles as well; min and max across the whole mesh would span every
        /// tile the donor touches.
        /// </summary>
        private static Rect UvRegion(Renderer renderer)
        {
            Rect whole = new Rect(0f, 0f, 1f, 1f);

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return whole;

            Vector2[] uv;
            int[] tris;
            try
            {
                uv = mesh.uv;
                tris = mesh.triangles;
            }
            catch (Exception)
            {
                return whole;
            }

            float bestArea = 0f;
            Rect best = whole;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector2 a = uv[tris[i]], b = uv[tris[i + 1]], c = uv[tris[i + 2]];
                float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                float width = maxX - minX, height = maxY - minY;

                if (width <= 0.005f || height <= 0.005f || width > 1f || height > 1f) continue;
                if (width * height <= bestArea) continue;

                bestArea = width * height;
                best = new Rect(minX, minY, width, height);
            }

            return best;
        }

        /// <summary>
        /// Each submesh's UVs squeezed into its material's slice of the atlas, always from
        /// the pristine coordinates so a second world does not squeeze the first one's.
        /// Clamped, never wrapped: wrapping a vertex that crosses 1.0 makes the GPU
        /// interpolate backwards across the whole tile.
        /// </summary>
        private static void Remap(Mesh mesh, Vector2[] source, Rect[] rects)
        {
            Vector2[] uv = (Vector2[])source.Clone();
            bool[] done = new bool[uv.Length];

            for (int i = 0; i < mesh.subMeshCount && i < rects.Length; i++)
            {
                Rect rect = rects[i];
                foreach (int index in mesh.GetTriangles(i))
                {
                    if (done[index]) continue;
                    done[index] = true;

                    uv[index] = new Vector2(
                        rect.x + Mathf.Clamp01(uv[index].x) * rect.width,
                        rect.y + Mathf.Clamp01(uv[index].y) * rect.height);
                }
            }

            mesh.uv = uv;
        }

        // ------------------------------------------------------------------ recipes

        private static void PlaceRecipes(ObjectDB db, GameObject table)
        {
            if (Wanted.Count == 0) return;

            CraftingStation station = table.GetComponent<CraftingStation>();
            if (station == null)
            {
                Once("station", "The registered " + PrefabName + " has no CraftingStation.");
                return;
            }

            foreach (Want want in Wanted) Place(db, station, want);
        }

        private static void Place(ObjectDB db, CraftingStation station, Want want)
        {
            ItemDrop basis = ItemOf(db, want.Base);
            ItemDrop infusing = ItemOf(db, want.Infusing);
            ItemDrop output = ItemOf(db, want.Output);

            string key = want.Base + "+" + want.Infusing + "=" + want.Output;
            if (basis == null || infusing == null || output == null)
            {
                Once("item:" + key, "Infusion recipe " + key + " skipped: no item named "
                    + (basis == null ? want.Base : infusing == null ? want.Infusing : want.Output)
                    + ". It is retried in case a mod registers the item later.");
                return;
            }

            if (basis.m_itemData.m_shared.m_maxQuality > 1)
            {
                Once("quality:" + key, "Infusion recipe " + key + " refused: " + want.Base
                    + " can be upgraded, and an infusion consumes it at any quality.");
                return;
            }

            string name = RecipeName(want.Output, want.Liquid);
            if (Exists(db, basis, infusing, output, name)) return;

            Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = name;
            recipe.m_item = output;
            recipe.m_amount = 1;
            recipe.m_craftingStation = station;
            recipe.m_resources = new[]
            {
                new Piece.Requirement { m_resItem = basis, m_amount = 1 },
                new Piece.Requirement { m_resItem = infusing, m_amount = 1 }
            };

            db.m_recipes.Add(recipe);
            Prefabs.Log.LogInfo("Infusion recipe added: " + key + ".");
        }

        /// <summary>
        /// Whether ObjectDB already holds this infusion, whoever added it: the same output at
        /// a table of this name, requiring both inputs and the same liquid. This is the whole
        /// of the "already added" test, so it holds across worlds and across mods without a
        /// flag.
        /// </summary>
        private static bool Exists(ObjectDB db, ItemDrop basis, ItemDrop infusing, ItemDrop output,
            string name)
        {
            foreach (Recipe recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_item != output || recipe.m_craftingStation == null
                    || recipe.m_craftingStation.m_name != DisplayName || recipe.name != name) continue;

                if (Requires(recipe, basis) && Requires(recipe, infusing)) return true;
            }

            return false;
        }

        private static bool Requires(Recipe recipe, ItemDrop item)
        {
            if (recipe.m_resources == null) return false;

            foreach (Piece.Requirement requirement in recipe.m_resources)
                if (requirement != null && requirement.m_resItem == item) return true;

            return false;
        }

        private static ItemDrop ItemOf(ObjectDB db, string name)
        {
            GameObject prefab = db.GetItemPrefab(name);
            return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        }

        // ------------------------------------------------------------------ the basin

        private static string RecipeName(string output, InfusionLiquid liquid)
        {
            return "Recipe_Infusion_" + output + "@" + liquid;
        }

        /// <summary>
        /// The liquid an infusion recipe asks for, read back off its name, or null for any
        /// other recipe. The name is the only place it is kept, so a patch in one linking mod
        /// still reads the recipes another linking mod added.
        /// </summary>
        private static InfusionLiquid? NeededBy(Recipe recipe)
        {
            if (recipe == null || recipe.name == null || !recipe.name.StartsWith("Recipe_Infusion_"))
                return null;

            int at = recipe.name.LastIndexOf('@');
            InfusionLiquid liquid;
            return at > 0 && Enum.TryParse(recipe.name.Substring(at + 1), out liquid)
                ? liquid : (InfusionLiquid?)null;
        }

        private static string Held(CraftingStation station)
        {
            ZNetView view = station != null ? station.GetComponentInParent<ZNetView>() : null;
            return view != null && view.IsValid() ? view.GetZDO().GetString(LiquidKey, "") : "";
        }

        private static void Drain(CraftingStation station)
        {
            ZNetView view = station.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid()) return;

            view.ClaimOwnership();
            view.GetZDO().Set(LiquidKey, "");
        }

        /// <summary>
        /// Two postfixes, applied once however many linked mods carry this file: whoever ticks
        /// first patches, and the rest see its patch on the method and leave it alone. Nothing
        /// they read lives in a static, so it does not matter whose copy that was.
        /// </summary>
        private static void Patch()
        {
            _patched = true;

            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                PatchOnce(harmony, AccessTools.Method(typeof(Player), "HaveRequirements",
                    new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) }),
                    nameof(GateByLiquid));
                PatchOnce(harmony, AccessTools.Method(typeof(Player), "ConsumeResources"),
                    nameof(EmptyBasinOnCraft));
            }
            catch (Exception e)
            {
                Prefabs.Log.LogError("Patching for the Infusion Table's liquid failed, so recipes "
                    + "will not need it: " + e);
            }
        }

        private static void PatchOnce(Harmony harmony, System.Reflection.MethodBase original,
            string postfix)
        {
            if (original == null)
            {
                Prefabs.Log.LogError("A Player method the Infusion Table patches is missing ("
                    + postfix + "). Recipes will not need the liquid.");
                return;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info != null)
                foreach (HarmonyLib.Patch patch in info.Postfixes)
                    if (patch.owner == HarmonyId) return;

            harmony.Patch(original, postfix: new HarmonyMethod(typeof(InfusionTable), postfix));
        }

        private static void GateByLiquid(Player __instance, Recipe recipe, bool discover,
            ref bool __result)
        {
            if (!__result || discover) return;

            InfusionLiquid? needed = NeededBy(recipe);
            if (needed == null) return;

            string held = Held(__instance.GetCurrentCraftingStation());
            InfusionLiquid liquid;
            __result = held != "" && Enum.TryParse(held, out liquid)
                && (needed == InfusionLiquid.Any || liquid == needed);
        }

        private static void EmptyBasinOnCraft(Player __instance, Piece.Requirement[] requirements)
        {
            CraftingStation station = __instance.GetCurrentCraftingStation();
            ObjectDB db = ObjectDB.instance;
            if (station == null || db == null || Held(station) == "") return;

            foreach (Recipe recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_resources != requirements || NeededBy(recipe) == null)
                    continue;

                Drain(station);
                return;
            }
        }

        /// <summary>
        /// What a placed table does that the CraftingStation does not: it shows what the
        /// basin holds, and takes a use of Resin, a Greydwarf Eye or Guck to fill it. It sits
        /// on the collider the player aims at, so it is the table's Interactable, and passes
        /// everything else on to the station. The liquid is a string on the table's ZDO, so
        /// it is saved with the world and every player sees the same basin.
        /// </summary>
        private sealed class InfusionBasin : MonoBehaviour, Interactable, Hoverable
        {
            private ZNetView _view;
            private CraftingStation _station;
            private GameObject _liquid;
            private Renderer _renderer;
            private Light _light;
            private Material _material;
            private string _shown;
            private float _nextCheck;

            private void Awake()
            {
                _view = GetComponentInParent<ZNetView>();
                _station = GetComponentInParent<CraftingStation>();

                Transform liquid = _view != null ? _view.transform.Find("infusion_liquid") : null;
                if (liquid == null) return;

                _liquid = liquid.gameObject;
                _renderer = liquid.GetComponent<Renderer>();
                _light = liquid.GetComponentInChildren<Light>(true);
                if (_renderer != null) _material = _renderer.material;
            }

            private void OnDestroy()
            {
                if (_material != null) Destroy(_material);
            }

            private void Update()
            {
                if (Time.time < _nextCheck) return;
                _nextCheck = Time.time + 0.25f;

                Show(Held(_station));
            }

            private void Show(string held)
            {
                if (held == _shown) return;
                _shown = held;
                if (_liquid == null) return;

                // An empty basin still shows its floor, dark and dull, because the model's
                // basin is open to the pedestal below without it.
                bool full = held != "";
                InfusionLiquid liquid;
                Color colour = !full ? DryColour
                    : Enum.TryParse(held, out liquid) ? ColourOf(liquid) : LiquidColour;

                if (_material != null)
                {
                    if (_material.HasProperty("_Color")) _material.SetColor("_Color", colour);
                    if (_material.HasProperty("_EmissionColor"))
                    {
                        // The donor's emission map is orange and would turn every colour orange.
                        _material.SetTexture("_EmissionMap", null);
                        _material.EnableKeyword("_EMISSION");
                        _material.SetColor("_EmissionColor", full ? colour * 0.45f : Color.black);
                    }
                }

                if (_light == null) return;
                _light.enabled = full;
                _light.color = colour;
            }

            public bool Interact(Humanoid user, bool hold, bool alt)
            {
                return _station != null && _station.Interact(user, hold, alt);
            }

            public bool UseItem(Humanoid user, ItemDrop.ItemData item)
            {
                InfusionLiquid? liquid = LiquidOfItem(item);
                if (liquid == null) return _station != null && _station.UseItem(user, item);
                if (_view == null || !_view.IsValid()) return false;

                if (Held(_station) != "")
                {
                    user.Message(MessageHud.MessageType.Center, "The basin is already full.");
                    return true;
                }

                if (!user.GetInventory().RemoveOneItem(item)) return false;

                _view.ClaimOwnership();
                _view.GetZDO().Set(LiquidKey, liquid.Value.ToString());
                user.Message(MessageHud.MessageType.Center, "You fill the basin.");
                return true;
            }

            public string GetHoverText()
            {
                string held = Held(_station);
                string status = held == ""
                    ? "Empty basin. Use Resin, a Greydwarf Eye or Guck on it to fill it."
                    : "The basin holds " + (held == "GreydwarfEye" ? "a Greydwarf Eye" : held) + ".";

                return (_station != null ? _station.GetHoverText() : "") + "\n" + status;
            }

            public string GetHoverName()
            {
                return _station != null ? _station.GetHoverName() : DisplayName;
            }

            public float GetHoverOffset()
            {
                return _station != null ? _station.GetHoverOffset() : 0f;
            }
        }

        // ------------------------------------------------------------------ assets

        private static string AssetPath(string file)
        {
            return Path.Combine(Path.GetDirectoryName(typeof(InfusionTable).Assembly.Location), file);
        }

        private sealed class Model
        {
            internal Mesh Mesh, Liquid;
            internal string[] Groups;
        }

        /// <summary>
        /// A Wavefront OBJ as a Mesh with one submesh per usemtl group, in file order, which
        /// map onto the renderer's materials in the same order. Reads what Blender writes:
        /// v, vt, vn and f in the v/vt/vn form.
        /// </summary>
        private static Model LoadModel(string path)
        {
            try
            {
                return Parse(File.ReadAllLines(path));
            }
            catch (Exception e)
            {
                Once("model-read", "Could not read " + path + ": " + e.Message);
                return null;
            }
        }

        private static Model Parse(string[] lines)
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            List<Vector3> positions = new List<Vector3>(), normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();

            List<string> groups = new List<string>();
            List<List<int>> triangles = new List<List<int>>();
            List<int> current = null;

            Dictionary<string, int> interned = new Dictionary<string, int>();
            List<Vector3> outPositions = new List<Vector3>(), outNormals = new List<Vector3>();
            List<Vector2> outUvs = new List<Vector2>();

            foreach (string raw in lines)
            {
                string[] parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0][0] == '#') continue;

                switch (parts[0])
                {
                    case "v":
                        positions.Add(new Vector3(float.Parse(parts[1], culture),
                            float.Parse(parts[2], culture), float.Parse(parts[3], culture)));
                        break;
                    case "vt":
                        uvs.Add(new Vector2(float.Parse(parts[1], culture), float.Parse(parts[2], culture)));
                        break;
                    case "vn":
                        normals.Add(new Vector3(float.Parse(parts[1], culture),
                            float.Parse(parts[2], culture), float.Parse(parts[3], culture)));
                        break;
                    case "usemtl":
                        int slot = groups.IndexOf(parts[1]);
                        if (slot < 0)
                        {
                            groups.Add(parts[1]);
                            triangles.Add(new List<int>());
                            slot = groups.Count - 1;
                        }

                        current = triangles[slot];
                        break;
                    case "f":
                        if (current == null) break;

                        // One flat vertex per distinct v/vt/vn corner, because the OBJ indexes
                        // the three separately and Unity cannot.
                        int[] corners = new int[parts.Length - 1];
                        for (int i = 1; i < parts.Length; i++)
                        {
                            int index;
                            if (!interned.TryGetValue(parts[i], out index))
                            {
                                string[] field = parts[i].Split('/');
                                outPositions.Add(positions[Resolve(field[0], positions.Count)]);
                                outUvs.Add(field.Length > 1 && field[1].Length > 0
                                    ? uvs[Resolve(field[1], uvs.Count)] : Vector2.zero);
                                outNormals.Add(field.Length > 2 && field[2].Length > 0
                                    ? normals[Resolve(field[2], normals.Count)] : Vector3.up);
                                index = outPositions.Count - 1;
                                interned[parts[i]] = index;
                            }

                            corners[i - 1] = index;
                        }

                        // Winding as exported. Reversing it turns the model inside out.
                        for (int i = 2; i < corners.Length; i++)
                        {
                            current.Add(corners[0]);
                            current.Add(corners[i - 1]);
                            current.Add(corners[i]);
                        }

                        break;
                }
            }

            if (outPositions.Count == 0) throw new InvalidDataException("no geometry");

            // The liquid is its own mesh so a table with an empty basin can simply not draw it.
            int liquidSlot = groups.FindIndex(IsLiquid);
            List<string> bodyGroups = new List<string>();
            List<List<int>> bodyTriangles = new List<List<int>>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i == liquidSlot) continue;
                bodyGroups.Add(groups[i]);
                bodyTriangles.Add(triangles[i]);
            }

            return new Model
            {
                Mesh = Assemble("infusion_table", outPositions, outUvs, outNormals, bodyTriangles),
                Groups = bodyGroups.ToArray(),
                Liquid = liquidSlot < 0 ? null : Assemble("infusion_liquid", outPositions, outUvs,
                    outNormals, new List<List<int>> { triangles[liquidSlot] })
            };
        }

        /// <summary>One Mesh from the vertices its triangles actually use, one submesh per list.</summary>
        private static Mesh Assemble(string name, List<Vector3> positions, List<Vector2> uvs,
            List<Vector3> normals, List<List<int>> triangles)
        {
            Dictionary<int, int> remap = new Dictionary<int, int>();
            List<Vector3> outPositions = new List<Vector3>(), outNormals = new List<Vector3>();
            List<Vector2> outUvs = new List<Vector2>();
            List<List<int>> outTriangles = new List<List<int>>();

            foreach (List<int> list in triangles)
            {
                List<int> mapped = new List<int>(list.Count);
                foreach (int index in list)
                {
                    int to;
                    if (!remap.TryGetValue(index, out to))
                    {
                        to = outPositions.Count;
                        remap[index] = to;
                        outPositions.Add(positions[index]);
                        outUvs.Add(uvs[index]);
                        outNormals.Add(normals[index]);
                    }

                    mapped.Add(to);
                }

                outTriangles.Add(mapped);
            }

            Mesh mesh = new Mesh { name = name };

            // UInt16 indices wrap silently past 65,535 vertices and draw garbage.
            if (outPositions.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(outPositions);
            mesh.SetUVs(0, outUvs);
            mesh.SetNormals(outNormals);
            mesh.subMeshCount = outTriangles.Count;
            for (int i = 0; i < outTriangles.Count; i++) mesh.SetTriangles(outTriangles[i], i);

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>OBJ indices are 1-based, and a negative one counts back from the end.</summary>
        private static int Resolve(string text, int count)
        {
            int value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return value > 0 ? value - 1 : count + value;
        }

        private static Sprite LoadIcon()
        {
            if (_icon != null) return _icon;

            string path = AssetPath(IconFile);
            if (!File.Exists(path))
            {
                Once("icon", IconFile + " is missing beside the dll (" + path
                    + "). The Infusion Table will use the workbench's icon.");
                return null;
            }

            try
            {
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                if (!LoadPng(texture, File.ReadAllBytes(path))) return null;

                texture.name = "infusion_table_icon";
                texture.hideFlags = HideFlags.HideAndDontSave;
                _icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                return _icon;
            }
            catch (Exception e)
            {
                Once("icon-read", "Could not read " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Texture2D.LoadImage, by reflection. It lives in UnityEngine.ImageConversionModule,
        /// which targets netstandard 2.1 and fails a net462 build with CS1705 if referenced.
        /// </summary>
        private static bool LoadPng(Texture2D texture, byte[] data)
        {
            Type type = AccessTools.TypeByName("UnityEngine.ImageConversion");
            System.Reflection.MethodInfo method = type == null ? null
                : AccessTools.Method(type, "LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

            if (method == null)
            {
                Once("loadimage", "UnityEngine.ImageConversion.LoadImage is missing, so the "
                    + "Infusion Table cannot read its icon.");
                return false;
            }

            return (bool)method.Invoke(null, new object[] { texture, data });
        }

        // ------------------------------------------------------------------ complaints

        /// <summary>
        /// Say it once. Everything that complains sits inside a Tick that runs every frame,
        /// and a missing file or item does not fix itself.
        /// </summary>
        private static void Once(string key, string message)
        {
            if (Told.Add(key)) Prefabs.Log.LogWarning(message);
        }
    }
}
