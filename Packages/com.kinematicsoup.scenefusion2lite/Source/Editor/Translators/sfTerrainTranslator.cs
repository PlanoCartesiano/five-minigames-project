using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.TerrainTools;
using KS.Reactor;
using KS.SceneFusion;
using KS.SceneFusion2.Client;
using UObject = UnityEngine.Object;
using System.Linq;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of terrain data.
     * The terrain data will be diveded into 64 by 64 regions and synced separately. When a region is modified, the
     * whole region's terrain data will be compressed and sent to the server. The server will keep a copy of the
     * of the data and send it to all other clients without decompressing it.
     */
    public class sfTerrainTranslator : sfBaseTranslator
    {
        private const int REGION_RESOLUTION = 64;
        private const int TREE_GROUP_SIZE = 1000;

        /**
         * Terrain type enum.
         */
        public enum TerrainType
        {
            HEIGHTMAP,
            ALPHAMAP,
            HOLES,
            TREES,
            DETAILS
        }

        /**
         * Applies an sfBaseProperty to terrain data.
         * 
         * @param   TerrainData terrainData to apply property change to.
         * @param   sfBaseProperty property to apply.
         */
        public delegate void PropertySetter(TerrainData terrainData, sfBaseProperty property);

        /**
         * Gets an sfBaseProperty from terrain data.
         * 
         * @param   TerrainData terrainData
         * @return  sfBaseProperty
         */
        private delegate sfBaseProperty PropertyGetter(TerrainData terrainData);

        /**
         * sfListProperty change event handler.
         * 
         * @param   TerrainData terrainData to apply property change to.
         * @param   sfBaseProperty property that changed.
         */
        public delegate void ListPropertyHandler(TerrainData terrainData, sfListProperty property, int index, int count);

        /**
         * Callback to use with terrain region iteration methods.
         * 
         * @param   RectInt region
         * @param   int regionIndex
         */
        public delegate void ForEachCallback(RectInt region, int regionIndex);

        // Map of property names to sfProperty setters
        private Dictionary<string, PropertySetter> m_setters =
            new Dictionary<string, PropertySetter>();

        // Map of property names to sfProperty getters
        private Dictionary<string, PropertyGetter> m_getters = new Dictionary<string, PropertyGetter>();

        // When the user changes these properties, resend all detail maps.
        private HashSet<string> m_sendDetailsOnChange = new HashSet<string>();

        // Map of property names to custom sfListProperty add event handlers
        private Dictionary<string, ListPropertyHandler> m_listPropertyAddHandlers =
            new Dictionary<string, ListPropertyHandler>();

        // Map of property names to custom sfListProperty remove event handlers
        private Dictionary<string, ListPropertyHandler> m_listPropertyRemoveHandlers =
            new Dictionary<string, ListPropertyHandler>();

        private Dictionary<TerrainData, HashSet<int>> m_dirtyHeightmapRegions
            = new Dictionary<TerrainData, HashSet<int>>();
        private Dictionary<TerrainData, HashSet<int>> m_dirtyAlphamapRegions
            = new Dictionary<TerrainData, HashSet<int>>();
        private Dictionary<TerrainData, HashSet<int>> m_dirtyHolesRegions
            = new Dictionary<TerrainData, HashSet<int>>();
        private Dictionary<TerrainData, Dictionary<int, HashSet<int>>> m_dirtyDetailRegions 
            = new Dictionary<TerrainData, Dictionary<int, HashSet<int>>>();

        // Keys are true if there may be tree removals.
        private Dictionary<TerrainData, bool> m_dirtyTrees = new Dictionary<TerrainData, bool>();

        private List<Terrain> m_terrains = new List<Terrain>();
        private List<TerrainCollider> m_tempDisabledColliders = new List<TerrainCollider>();

        // We use this hashset to track which terrain objects are applying updates recevied from the server and
        // prevent those update from triggering a send back to the server.
        private HashSet<TerrainData> m_applyingServerUpdates = new HashSet<TerrainData>();

        // Classes used to handle serialization and deserialization of terrain data types.
        private sfDetailPrototypeSync m_detailPrototypeSync = new sfDetailPrototypeSync();
        private sfTreePrototypeSync m_treePrototypeSync = new sfTreePrototypeSync();
        private sfTreeInstanceSync m_treeInstanceSync = new sfTreeInstanceSync();

        private sfITerrainCompressor m_compressor = new sfLZWTerrainCompressor();
        private float m_sendChangesTimer;

        /**
         *  Initialization
         */
        public override void Initialize()
        {
            RegisterPropertyHandlers();

            sfComponentTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfComponentTranslator>(
                sfType.Component);
            translator.ObjectInitializers.Add<Terrain>(OnInitializeTerrainComponent);
            translator.ComponentInitializers.Add<Terrain>(OnInitializeTerrainComponent);
            translator.DeleteHandlers.Add<Terrain>(OnDeleteTerrainComponent);
            translator.LocalDeleteHandlers.Add<Terrain>(OnLocalDeleteTerrainComponent);

            sfPropertyManager.Get().SyncedHiddenProperties.Add<Terrain>("m_ScaleInLightmap");
#if UNITY_2022_2_OR_NEWER
            sfPropertyManager.Get().SyncedHiddenProperties.Add<Terrain>("m_ReceiveGI");
#endif
        }

        /**
         * Registers property handlers.
         */
        private void RegisterPropertyHandlers()
        {
            RegisterProperty(sfProp.TerrainSize,
                (TerrainData terrainData) => sfValueProperty.From(terrainData.size),
                (TerrainData terrainData, sfBaseProperty prop) =>
                {
                    terrainData.size = prop.As<Vector3>();
                    // We have to call Flush on all the terrain components using this terrain data to get the tree
                    // positions to update to fit the new terrain bounds.
                    foreach (Terrain terrain in IterateTerrainComponents(terrainData))
                    {
                        terrain.Flush();
                    }
                });
            RegisterProperty(sfProp.DetailResolution,
                (TerrainData terrainData) => new sfValueProperty(terrainData.detailResolution),
                (TerrainData terrainData, sfBaseProperty prop) => 
                    terrainData.SetDetailResolution((int)prop, terrainData.detailResolutionPerPatch));
            RegisterProperty(sfProp.DetailPatchResolution,
                (TerrainData terrainData) => new sfValueProperty(terrainData.detailResolutionPerPatch),
                (TerrainData terrainData, sfBaseProperty prop) =>
                    terrainData.SetDetailResolution(terrainData.detailResolution, (int)prop));
#if UNITY_2022_2_OR_NEWER
            RegisterProperty(sfProp.DetailScatterMode,
                (TerrainData terrainData) => new sfValueProperty((int)terrainData.detailScatterMode),
                (TerrainData terrainData, sfBaseProperty prop) => 
                    terrainData.SetDetailScatterMode((DetailScatterMode)(int)prop));
#endif
            RegisterProperty(sfProp.WavingGrassAmount,
                (TerrainData terrainData) => new sfValueProperty(terrainData.wavingGrassAmount),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.wavingGrassAmount = (float)prop);
            RegisterProperty(sfProp.WavingGrassSpeed,
                (TerrainData terrainData) => new sfValueProperty(terrainData.wavingGrassSpeed),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.wavingGrassSpeed = (float)prop);
            RegisterProperty(sfProp.WavingGrassStrength,
                (TerrainData terrainData) => new sfValueProperty(terrainData.wavingGrassStrength),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.wavingGrassStrength = (float)prop);
            RegisterProperty(sfProp.WavingGrassTint,
                (TerrainData terrainData) => new sfValueProperty(terrainData.wavingGrassTint),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.wavingGrassTint = prop.As<Color>());
            RegisterProperty(sfProp.CompressHoles,
                (TerrainData terrainData) => new sfValueProperty(terrainData.enableHolesTextureCompression),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.enableHolesTextureCompression = (bool)prop);
            RegisterProperty(sfProp.BaseMapResolution,
                (TerrainData terrainData) => new sfValueProperty(terrainData.baseMapResolution),
                (TerrainData terrainData, sfBaseProperty prop) => terrainData.baseMapResolution = (int)prop);

            // When the user changes these properties, resend all detail maps.
            m_sendDetailsOnChange.Add(sfProp.DetailResolution);
            m_sendDetailsOnChange.Add(sfProp.DetailPatchResolution);
#if UNITY_2022_2_OR_NEWER
            m_sendDetailsOnChange.Add(sfProp.DetailScatterMode);
#endif

            // The following properties do not have getter delegates, as they have custom-logic around when and how the
            // value changes.

            m_setters[sfProp.HeightmapResolution] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                // Setting the heightmap resolution via code changes the size for some reason, but changing it in the
                // inspector does not. We reset the size after setting the resolution.
                Vector3 size = terrainData.size;
                terrainData.heightmapResolution = (int)property;
                terrainData.size = size;
            };
            m_setters[sfProp.AlphamapResolution] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                terrainData.alphamapResolution = (int)property;
            };

            m_setters[sfProp.TerrainLayerPalette] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyTerrainLayers(terrainData, property as sfListProperty);
                }
                else
                {
                    OnServerTerrainLayersChange(terrainData, property);
                }
            };
            m_setters[sfProp.DetailPrototypePalette] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyDetailPrototypes(terrainData, property as sfListProperty);
                }
                else
                {
                    OnServerDetailPrototypeChange(terrainData, property);
                }
            };
            m_setters[sfProp.TreePrototypePalette] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyTreePrototypes(terrainData, property as sfListProperty);
                }
                else
                {
                    OnServerTreePrototypeChange(terrainData, property);
                }
            };
            m_setters[sfProp.Heightmap] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyServerTerrainData(terrainData, TerrainType.HEIGHTMAP, property);
                }
                else if (property.Type == sfBaseProperty.Types.VALUE)
                {
                    OnServerTerainChange(terrainData, TerrainType.HEIGHTMAP, property);
                }
            };
            m_setters[sfProp.Alphamap] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyServerTerrainData(terrainData, TerrainType.ALPHAMAP, property);
                }
                else if (property.Type == sfBaseProperty.Types.VALUE)
                {
                    OnServerTerainChange(terrainData, TerrainType.ALPHAMAP, property);
                }
            };
            m_setters[sfProp.Holes] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (property.Type == sfBaseProperty.Types.LIST)
                {
                    ApplyServerTerrainData(terrainData, TerrainType.HOLES, property);
                }
                else if (property.Type == sfBaseProperty.Types.VALUE)
                {
                    OnServerTerainChange(terrainData, TerrainType.HOLES, property);
                }
            };
            m_setters[sfProp.Details] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (!string.IsNullOrEmpty(property.Name))
                {
                    // If the property is named, then it is the complete list of all detail layers
                    ApplyServerDetails(terrainData, property as sfListProperty);
                }
                else if (property.Type == sfBaseProperty.Types.LIST)
                {
                    // If the property is not named and is a list, then it is a complete layer list
                    ApplyServerDetailLayer(terrainData, property as sfListProperty, property.Index);
                }
                else if (property.Type == sfBaseProperty.Types.VALUE)
                {
                    // If the property is a value property, then is contain detail data of a single region in a single layer
                    OnServerDetailChange(terrainData, property);
                }
            };
            m_setters[sfProp.Trees] = delegate (TerrainData terrainData, sfBaseProperty property)
            {
                if (!string.IsNullOrEmpty(property.Name))
                {
                    ApplyServerTrees(terrainData, property as sfListProperty);
                }
                else
                {
                    ApplyServerTrees(terrainData, property.ParentProperty as sfListProperty);
                }
            };

            // List Adders
            m_listPropertyAddHandlers[sfProp.TerrainLayerPalette] =
                delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyTerrainLayers(terrainData, property);
                sfTerrainEditor.AdjustLayerIndex(terrainData, true, index, count);
            };
            m_listPropertyAddHandlers[sfProp.DetailPrototypePalette] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyDetailPrototypes(terrainData, property);
                sfTerrainEditor.AdjustDetailIndex(terrainData, true, index, count);
            };
            m_listPropertyAddHandlers[sfProp.TreePrototypePalette] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyTreePrototypes(terrainData, property);
            };
            m_listPropertyAddHandlers[sfProp.Details] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                // We have to reapply every layer after the inserted layer since Unity does not let us insert a layer.
                for (int i = index; i < property.Count; i++)
                {
                    ApplyServerDetailLayer(terrainData, property[i] as sfListProperty, i);
                }
            };
            m_listPropertyAddHandlers[sfProp.Trees] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                 ApplyServerTrees(terrainData, property);
            };

            // List Removers
            m_listPropertyRemoveHandlers[sfProp.TerrainLayerPalette] = 
                delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyTerrainLayers(terrainData, property);
                sfTerrainEditor.AdjustLayerIndex(terrainData, false, index, count);
            };
            m_listPropertyRemoveHandlers[sfProp.DetailPrototypePalette] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
#if UNITY_2022_2_OR_NEWER
                for (int i = 0; i < count; i++)
                {
                    terrainData.RemoveDetailPrototype(index);
                }
#else
                ApplyTerrainLayers(terrainData, property);
#endif
                sfTerrainEditor.AdjustDetailIndex(terrainData, false, index, count);
            };
            m_listPropertyRemoveHandlers[sfProp.TreePrototypePalette] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyTreePrototypes(terrainData, property);
            };
            m_listPropertyRemoveHandlers[sfProp.Trees] = delegate (TerrainData terrainData, sfListProperty property, int index, int count)
            {
                ApplyServerTrees(terrainData, property);
            };
        }

        /**
         * Registers a getter and setter for syncing a terrain data property.
         * 
         * @param   string name of the property.
         * @param   PropertyGetter getter
         * @param   PropertySetter setter
         */
        private void RegisterProperty(string name, PropertyGetter getter, PropertySetter setter)
        {
            m_getters[name] = getter;
            m_setters[name] = setter;
        }

        /**
         * Called when a terrain component is initialized for syncing. Adds the terrain to the list of synced terrains.
         * 
         * @param   sfObject obj for the terrain
         * @param   Component component - terrain component
         */
        private void OnInitializeTerrainComponent(sfObject obj, Component component)
        {
            m_terrains.Add((Terrain)component);
        }

        /**
         * Called when a terrain component is deleted by another user. Removes the terrain from the list of synced
         * terrains.
         * 
         * @param   sfObject obj for the terrain
         * @param   Component component - terrain component
         */
        private void OnDeleteTerrainComponent(sfObject obj, Component component)
        {
            for (int i = m_terrains.Count - 1; i >= 0; i--)
            {
                if (m_terrains[i] == component)
                {
                    m_terrains.RemoveAt(i);
                }
            }
        }

        /**
         * Called when a terrain component is deleted by the local user. Removes null terrains from the list of synced
         * terrains.
         * 
         * @param   sfObject obj for the terrain
         */
        private void OnLocalDeleteTerrainComponent(sfObject obj)
        {
            for (int i = m_terrains.Count - 1; i >= 0; i--)
            {
                if (m_terrains[i] == null)
                {
                    m_terrains.RemoveAt(i);
                }
            }
        }

        /**
         * Called after connecting to a session.
         */
        public override void OnSessionConnect()
        {
            if (SceneFusion.Get().Service.Session.GetObjectLimit(sfType.Terrain) == 0)
            {
                sfTerrainEditor.State = sfTerrainEditor.States.DISABLED;
                return;
            }
            sfTerrainEditor.State = sfTerrainEditor.States.OVERRIDE;
            m_sendChangesTimer = 1f / sfConfig.Get().Performance.TerrainSyncRate;
            SceneFusion.Get().OnUpdate += Update;
            sfTerrainEditor.PreSceneGUI += PreSceneGUI;
            sfTerrainEditor.PostSceneGUI += PostSceneGUI;
            sfUnityEventDispatcher.Get().OnTerrainHeightmapChange += OnHeightmapChange;
            sfUnityEventDispatcher.Get().OnTerrainTextureChange += OnTextureChange;
            sfUnityEventDispatcher.Get().OnTerrainDetailChange += OnDetailChange;
            sfUnityEventDispatcher.Get().OnTerrainTreeChange += OnTreeChange;
            sfUnityEventDispatcher.Get().OnTerrainCheck += CheckTerrain;
        }

        /**
         * Called after disconnecting from a session.
         */
        public override void OnSessionDisconnect()
        {
            if (sfTerrainEditor.State == sfTerrainEditor.States.OVERRIDE)
            {
                m_terrains.Clear();
                m_dirtyHeightmapRegions.Clear();
                m_dirtyAlphamapRegions.Clear();
                m_dirtyHolesRegions.Clear();

                SceneFusion.Get().OnUpdate -= Update;
                sfTerrainEditor.PreSceneGUI -= PreSceneGUI;
                sfTerrainEditor.PostSceneGUI -= PostSceneGUI;
                sfUnityEventDispatcher.Get().OnTerrainHeightmapChange -= OnHeightmapChange;
                sfUnityEventDispatcher.Get().OnTerrainTextureChange -= OnTextureChange;
                sfUnityEventDispatcher.Get().OnTerrainDetailChange -= OnDetailChange;
                sfUnityEventDispatcher.Get().OnTerrainTreeChange -= OnTreeChange;
                sfUnityEventDispatcher.Get().OnTerrainCheck -= CheckTerrain;
            }
            sfTerrainEditor.State = sfTerrainEditor.States.BASE;
        }

        /**
         * Creates an sfObject for a uobject if the object is a terrain data asset.
         *
         * @param   UObject uobj to create sfObject for.
         * @param   sfObject outObj created for the uobject.
         * @return  bool true if the uobject was handled by this translator.
         */
        public override bool TryCreate(UObject uobj, out sfObject outObj)
        {
            TerrainData terrainData = uobj as TerrainData;
            if (terrainData == null)
            {
                outObj = null;
                return false;
            }
            if (sfTerrainEditor.State == sfTerrainEditor.States.OVERRIDE)
            {
                outObj = CreateObject(terrainData);
                if (outObj != null)
                {
                    SceneFusion.Get().Service.Session.Create(outObj);
                }
            }
            else
            {
                outObj = null;
            }
            return true;
        }

        /**
         * Called when an object is created by another user.
         *
         * @param   sfObject obj that was created.
         * @param   int childIndex of new object. -1 if object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            sfDictionaryProperty dict = obj.Property as sfDictionaryProperty;
            sfAssetInfo assetInfo = sfPropertyUtils.GetAssetInfo(dict, typeof(TerrainData));
            sfBaseProperty prop;
            string guid = dict.TryGetField(sfProp.Guid, out prop) ? (string)prop : null;
            TerrainData terrainData = sfLoader.Get().Load<TerrainData>(assetInfo, guid);
            if (terrainData == null)
            {
                return;
            }

            // Track when we are applying server state to the terrain data so we can filter
            // and ignore unity events that are triggered by the update.
            m_applyingServerUpdates.Add(terrainData);
            sfObjectMap.Get().Add(obj, terrainData);
            // Resolution needs to be set before we apply height/alpha/detail data.
            terrainData.heightmapResolution = (int)dict[sfProp.HeightmapResolution];
            terrainData.alphamapResolution = (int)dict[sfProp.AlphamapResolution];
            terrainData.SetDetailResolution((int)dict[sfProp.DetailResolution], (int)dict[sfProp.DetailPatchResolution]);
            foreach (KeyValuePair<string, PropertySetter> setter in m_setters)
            {
                switch (setter.Key)
                {
                    case sfProp.HeightmapResolution:
                    case sfProp.AlphamapResolution:
                    case sfProp.DetailResolution:
                    case sfProp.DetailPatchResolution:
                        continue;
                }
                setter.Value(terrainData, dict[setter.Key]);
            }

            m_applyingServerUpdates.Remove(terrainData);
            sfUI.Get().MarkSceneViewStale();
            sfUI.Get().MarkProjectBrowserStale();
        }

        /**
         * Called when a locally created terrain object is confirmed as created. Refreshes the project browser to
         * update the icon for the terrain asset.
         * 
         * @param   sfObject obj that whose creation was confirmed.
         */
        public override void OnConfirmCreate(sfObject obj)
        {
            sfUI.Get().MarkProjectBrowserStale();
        }

        /**
         * Called when an object is deleted by another user.
         *
         * @param   sfObject obj that was deleted.
         */
        public override void OnDelete(sfObject obj)
        {
            TerrainData terrainData = sfObjectMap.Get().Get<TerrainData>(obj);
            if (terrainData != null)
            {
                m_dirtyHeightmapRegions.Remove(terrainData);
                m_dirtyAlphamapRegions.Remove(terrainData);
                m_dirtyHolesRegions.Remove(terrainData);
            }
        }

        /**
         * Called when an object property changes.
         *
         * @param   sfBaseProperty property that changed.
         */
        public override void OnPropertyChange(sfBaseProperty property)
        {
            sfObject obj = property.GetContainerObject();
            if (obj == null)
            {
                return;
            }

            TerrainData terrainData = sfObjectMap.Get().Get<TerrainData>(obj);
            if (terrainData == null)
            {
                return;
            }

            // Iterate property ancestors to until a property handler is found.
            PropertySetter handler = null;
            sfBaseProperty ancestorProperty = property;
            while (ancestorProperty != null)
            {
                if (ancestorProperty.Name != null && m_setters.TryGetValue(ancestorProperty.Name, out handler))
                {
                    handler(terrainData, property);

                    // Refresh the inspector for the terrain
                    foreach (Terrain terrain in IterateTerrainComponents(terrainData))
                    {
                        sfUI.Get().MarkInspectorStale(terrain);
                    }
                    return;
                }
                ancestorProperty = ancestorProperty.ParentProperty;
            }
            ksLog.Warning(this, "No property change handler for " + property.GetPath());
        }

        /**
         * Called when an a property is added to an object list property.
         *
         * @param   sfListProperty - list property that had values added to it.
         * @param   int - index of the first property added
         * @param   count - number of properties added
         */
        public override void OnListAdd(sfListProperty listProperty, int index, int count)
        {
            ListPropertyHandler handler = null;
            if (m_listPropertyAddHandlers.TryGetValue(listProperty.Name, out handler))
            {
                sfObject obj = listProperty.GetContainerObject();
                TerrainData terrainData = sfObjectMap.Get().Get<TerrainData>(obj);
                if (terrainData != null)
                {
                    handler(terrainData, listProperty, index, count);

                    // Refresh the inspector for the terrain
                    foreach (Terrain terrain in IterateTerrainComponents(terrainData))
                    {
                        sfUI.Get().MarkInspectorStale(terrain);
                    }
                }
            }
        }

        /**
         * Called when an a property is removed from an object list property.
         *
         * @param   sfListProperty - list property that had values removed from it.
         * @param   int - index of the first property removed
         * @param   count - number of properties removed
         */
        public override void OnListRemove(sfListProperty listProperty, int index, int count)
        {
            ListPropertyHandler handler = null;
            if (m_listPropertyRemoveHandlers.TryGetValue(listProperty.Name, out handler))
            {
                sfObject obj = listProperty.GetContainerObject();
                TerrainData terrainData = sfObjectMap.Get().Get<TerrainData>(obj);
                if (terrainData != null)
                {
                    handler(terrainData, listProperty, index, count);

                    // Refresh the inspector for the terrain
                    foreach (Terrain terrain in IterateTerrainComponents(terrainData))
                    {
                        sfUI.Get().MarkInspectorStale(terrain);
                    }
                }
            }
        }

        /**
         * Called when a terrain changes on the server. Applies changes to the terrain data.
         *
         * @param   Terrain terrain to apply change
         * @param   TerrainType type
         * @param   sfBaseProperty property
         */
        private void OnServerTerainChange(TerrainData terrainData, TerrainType type, sfBaseProperty property)
        {
            int resolution = GetResolution(terrainData, type);
            RectInt region = GetRegion(property.Index, resolution);
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    // Heightmap paint actions are applied instantly so we don't have to apply delayed actions.
                    ApplyServerHeightmapRegionData(terrainData, property, region);
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    // Prevent local user's unapplied paint texture actions from being lost.
                    sfUnityEventDispatcher.Get().TempEnable(PaintContext.ApplyDelayedActions);
                    ApplyServerAlphamapRegionData(terrainData, property, region);
                    break;
                }
                case TerrainType.HOLES:
                {
                    // Prevent local user's unapplied paint holes actions from being lost.
                    sfUnityEventDispatcher.Get().TempEnable(PaintContext.ApplyDelayedActions);
                    ApplyServerHolesRegionData(terrainData, property, region);
                    break;
                }
            }
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply new detail region data to the terrain data object.
         * 
         * @param   TerrainData - Terrain data object to update.
         * @param   sfBaseProperty - Property containing the detail region data.
         */
        private void OnServerDetailChange(TerrainData terrainData, sfBaseProperty property)
        {
            RectInt region = GetRegion(property.Index, GetResolution(terrainData, TerrainType.DETAILS));
            ApplyServerDetailRegionData(terrainData, property, region, property.ParentProperty.Index);
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Called every frame.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void Update(float deltaTime)
        {
            m_sendChangesTimer -= deltaTime;
            if (m_sendChangesTimer > 0f)
            {
                return;
            }
            m_sendChangesTimer = 1f / sfConfig.Get().Performance.TerrainSyncRate;
            SendTerrainChanges(TerrainType.HEIGHTMAP);
            SendTerrainChanges(TerrainType.ALPHAMAP);
            SendTerrainChanges(TerrainType.HOLES);
            SendDetailChanges();
            SendTreeChanges();
        }

        /**
         * Called before the terrain editor scene gui is drawn. If the tree tool is selected, temporarily disables
         * terrain colliders for locked terrains so the brush preview will not render on them and they cannot be
         * painted on.
         */
        private void PreSceneGUI()
        {
            if (sfTerrainEditor.Tool == sfTerrainEditor.Tools.PLACE_TREE)
            {
                foreach (Terrain terrain in m_terrains)
                {
                    sfObject obj = sfObjectMap.Get().GetSFObject(terrain);
                    if (obj != null && obj.IsLocked)
                    {
                        TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
                        if (collider != null && collider.enabled)
                        {
                            collider.enabled = false;
                            m_tempDisabledColliders.Add(collider);
                        }
                    }
                }
            }
        }

        /**
         * Called after the terrain editor scene gui is drawn. Re-enables temporarily disabled terrain colliders.
         */
        private void PostSceneGUI()
        {
            for (int i = 0; i < m_tempDisabledColliders.Count; i++)
            {
                m_tempDisabledColliders[i].enabled = true;
            }
            m_tempDisabledColliders.Clear();
        }

        /**
         * Creates an sfObject for the given terrain data.
         * 
         * @param   TerrainData terrainData
         * @return  sfObject
         */
        private sfObject CreateObject(TerrainData terrainData)
        {
            if (terrainData == null)
            {
                return null;
            }
            sfAssetInfo assetInfo = sfLoader.Get().GetAssetInfo(terrainData);
            if (!assetInfo.IsValid)
            {
                return null;
            }
            sfObject obj = sfObjectMap.Get().GetOrCreateSFObject(terrainData, sfType.Terrain);
            if (obj.IsSyncing)
            {
                return null;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)obj.Property;
            sfPropertyUtils.SetAssetInfoProperties(dict, assetInfo, false);
            string guid;
            long fileId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(terrainData, out guid, out fileId))
            {
                dict[sfProp.Guid] = guid;
            }

            dict[sfProp.HeightmapResolution] = new sfValueProperty(terrainData.heightmapResolution);
            dict[sfProp.AlphamapResolution] = new sfValueProperty(terrainData.alphamapResolution);
            dict[sfProp.TerrainLayerPalette] = SerializeTerrainLayerPalette(terrainData);
            dict[sfProp.DetailPrototypePalette] = SerializeDetailPrototypePalette(terrainData);
            dict[sfProp.TreePrototypePalette] = SerializeTreePrototypePalette(terrainData);
            dict[sfProp.Heightmap] = SerializeTerrainData(terrainData, TerrainType.HEIGHTMAP);
            dict[sfProp.Alphamap] = SerializeTerrainData(terrainData, TerrainType.ALPHAMAP);
            dict[sfProp.Holes] = SerializeTerrainData(terrainData, TerrainType.HOLES);
            dict[sfProp.Details] = SerializeDetails(terrainData);
            dict[sfProp.Trees] = SerializeTrees(terrainData);

            foreach (KeyValuePair<string, PropertyGetter> getter in m_getters)
            {
                dict[getter.Key] = getter.Value(terrainData);
            }

            return obj;
        }

        /**
         * Serialize the terrain layer asset paths in the terrain data object.
         * 
         * @param   TerrainData - Terrain data containing the terrain layers.
         * @return  sfListProperty - Serialized list of terrain layer assets.
         */
        private sfListProperty SerializeTerrainLayerPalette(TerrainData terrainData)
        {
            sfListProperty props = new sfListProperty();
            TerrainLayer[] layers = terrainData.terrainLayers;
            for (int i=0; i< layers.Length; i++)
            {
                TerrainLayer layer = layers[i];
                props.Add(sfPropertyUtils.FromReference(layer));
            }
            return props;
        }

        /**
         * Serializes the terrain detail prototypes in the terrain data object.
         * 
         * @param   TerrainData - Terrain data containing the detail prototypes
         * @return  sfListProperty - Serialized list of detail prototypes
         */
        private sfListProperty SerializeDetailPrototypePalette(TerrainData terrainData)
        {
            sfListProperty props = new sfListProperty();
            DetailPrototype[] prototypes = terrainData.detailPrototypes;
            bool updated = false;
            for (int i = 0; i < prototypes.Length; i++)
            {
                props.Add(m_detailPrototypeSync.Serialize(prototypes[i], ref updated));
            }
            if (updated)
            {
                terrainData.detailPrototypes = prototypes;
            }
            return props;
        }

        /**
         * Serializes the terrain tree prototypes in the terrain data object.
         * 
         * @param   TerrainData - Terrain data containing the tree prototypes
         * @return  sfListProperty - Serialized list of tree prototypes
         */
        private sfListProperty SerializeTreePrototypePalette(TerrainData terrainData)
        {
            sfListProperty props = new sfListProperty();
            TreePrototype[] prototypes = terrainData.treePrototypes;
            bool updated = false;
            for (int i = 0; i < prototypes.Length; i++)
            {
                props.Add(m_treePrototypeSync.Serialize(prototypes[i],ref updated));
            }
            if (updated)
            {
                terrainData.treePrototypes = prototypes;
            }
            return props;
        }

        /**
         * Serialize the terrain data of the given type.
         * 
         * @param   TerrainData terrainData
         * @param   TerrainType type
         * @return  sfListProperty
         */
        private sfListProperty SerializeTerrainData(TerrainData terrainData, TerrainType type)
        {
            // Create terrain heightmap region properties
            sfListProperty regionProps = new sfListProperty();
            ForEachCallback callback = null;
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        regionProps.Add(SerializeHeightmapRegion(terrainData, region));
                    };
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        regionProps.Add(SerializeAlphamapRegion(terrainData, region));
                    };
                    break;
                }
                case TerrainType.HOLES:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        regionProps.Add(SerializeHolesRegion(terrainData, region));
                    };
                    break;
                }
            }
            ForEachTerrainRegion(terrainData, type, callback);
            return regionProps;
        }

        /**
         * Each detail layer has its own set of region data
         */
        private sfListProperty SerializeDetails(TerrainData terrainData)
        {
            sfListProperty detailLayerProps = new sfListProperty();
            for (int i = 0; i < terrainData.detailPrototypes.Length; ++i)
            {
                sfListProperty regionProps = new sfListProperty();
                ForEachTerrainRegion(terrainData, TerrainType.DETAILS, (RectInt region, int regionIndex) =>
                {
                    regionProps.Add(SerializeDetailRegion(terrainData, region, i));
                });
                detailLayerProps.Add(regionProps);
            }
            return detailLayerProps;
         }

        /**
         * Serialize all tree data
         * 
         * @param   TerrainData - Terrain data holding the trees to sync.
         */
        private sfListProperty SerializeTrees(TerrainData terrainData)
        {
            sfListProperty treesProp = new sfListProperty();
            TreeInstance[] trees = terrainData.treeInstances;

            // First tree property is the number of trees. This is needed for decoding.
            treesProp.Add(new sfValueProperty(trees.Length));

            // Sync trees in groups as byte arrays
            for (int i=0; i<trees.Length; i+=TREE_GROUP_SIZE)
            {
                int count = Math.Min(TREE_GROUP_SIZE, trees.Length - i);
                if (count > 0)
                {
                    byte[] data = m_treeInstanceSync.Serialize(trees, i, count);
                    treesProp.Add(new sfValueProperty(m_compressor.EncodeTrees(data)));
                }
            }
            return treesProp;
        }

        /**
         * Serialize the heightmap data of the given terrain data in the given region.
         * 
         * @param   TerrainData terrainData to set
         * @param   RectInt region of heightmap to serialize
         * @return  sfValueProperty
         */
        private sfValueProperty SerializeHeightmapRegion(TerrainData terrainData, RectInt region)
        {
            float[,] heightmapData = terrainData.GetHeights(region.x, region.y, region.width, region.height);
            return new sfValueProperty(m_compressor.EncodeHeightmap(heightmapData));
        }

        /**
         * Serialize the alphamap data of the given terrain data in the given region.
         * 
         * @param   TerrainData terrainData to set
         * @param   RectInt region of alphamap to serialize
         * @return  sfValueProperty
         */
        private sfValueProperty SerializeAlphamapRegion(TerrainData terrainData, RectInt region)
        {
            float[,,] alphaData = terrainData.GetAlphamaps(region.x, region.y, region.width, region.height);
            return new sfValueProperty(m_compressor.EncodeAlphamap(alphaData));
        }

        /**
         * Serialize the holes data of the given terrain data in the given region.
         * 
         * @param   TerrainData terrainData to set
         * @param   RectInt region of terrain holes to serialize
         * @return  sfValueProperty
         */
        private sfValueProperty SerializeHolesRegion(TerrainData terrainData, RectInt region)
        {
            bool[,] holesData = terrainData.GetHoles(region.x, region.y, region.width, region.height);
            bool[] raw = new bool[holesData.Length];
            Buffer.BlockCopy(holesData, 0, raw, 0, holesData.Length);
            List<byte> encoded = sfRunLength<bool>.Encode(raw);
            return new sfValueProperty(encoded.ToArray());
        }

        /**
         * Serialize the detail layer data of a given terrain data in the given region.
         * 
         * @param   TerrainData - Terrain data containing the data
         * @param   RectInt - region of terrain details to serialize
         * @return  sfValueProperty
         */
        private sfValueProperty SerializeDetailRegion(TerrainData terrainData, RectInt region, int layer)
        {
            int[,] detailData = terrainData.GetDetailLayer(region.x, region.y, region.width, region.height, layer);
            byte[] encoded = m_compressor.EncodeDetailLayer(detailData);
            return new sfValueProperty(encoded);
        }

        /**
         * Rebuild the terrain layer array to match a the list of assets references.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of terrain layer assets
         */
        private void ApplyTerrainLayers(TerrainData terrainData, sfListProperty list)
        {
            TerrainLayer[] layers = new TerrainLayer[list.Count];
            for (int i = 0; i < list.Count; ++i)
            {
                layers[i] = sfPropertyUtils.ToReference(list[i], layers[i]);
            }
            terrainData.terrainLayers = layers;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Rebuild the detail prototypes to match the list of detail data.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of detail prototypes data
         */
        private void ApplyDetailPrototypes(TerrainData terrainData, sfListProperty list)
        {
            DetailPrototype[] prototypes = new DetailPrototype[list.Count];
            for (int i = 0; i < list.Count; ++i)
            {
                prototypes[i] = m_detailPrototypeSync.Deserialize(list[i] as sfDictionaryProperty);
            }
            terrainData.detailPrototypes = prototypes;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply updates on a terrain layer to the terrain data.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfBaseProperty - layer data
         */
        private void OnServerTerrainLayersChange(TerrainData terrainData, sfBaseProperty property)
        {
            TerrainLayer[] layers = terrainData.terrainLayers;
            layers[property.Index] = sfPropertyUtils.ToReference(property, layers[property.Index]);
            terrainData.terrainLayers = layers;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply updates on a detail prototype to the terrain data.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfBaseProperty - Detail prototype data
         */
        private void OnServerDetailPrototypeChange(TerrainData terrainData, sfBaseProperty property)
        {
            // If the terrain data detail prototypes list is not reassigned completely then
            // the next time a user selects the terrain, the prototype will switch back to its
            // previous state.
            if (property.Type != sfBaseProperty.Types.DICTIONARY)
            {
                property = property.ParentProperty;
            }
            DetailPrototype[] prototypes = terrainData.detailPrototypes;
            prototypes[property.Index] = m_detailPrototypeSync.Deserialize(property as sfDictionaryProperty);
            terrainData.detailPrototypes = prototypes;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Rebuild the detail prototypes to match the list of assets references.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of tree prototypes data
         */
        private void ApplyTreePrototypes(TerrainData terrainData, sfListProperty list)
        {
            TreePrototype[] prototypes = new TreePrototype[list.Count];
            for (int i = 0; i < list.Count; ++i)
            {
                prototypes[i] = m_treePrototypeSync.Deserialize(list[i] as sfDictionaryProperty);
            }
            terrainData.treePrototypes = prototypes;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply updates on a detail prototype to the terrain data.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfBaseProperty - Tree prototype data
         */
        private void OnServerTreePrototypeChange(TerrainData terrainData, sfBaseProperty property)
        {
            // If the terrain data detail prototypes list is not reassigned completely then
            // the next time a user selects the terrain, the prototype will switch back to its
            // previous state.
            if (property.Type != sfBaseProperty.Types.DICTIONARY)
            {
                property = property.ParentProperty;
            }
            TreePrototype[] prototypes = terrainData.treePrototypes;
            prototypes[property.Index] = m_treePrototypeSync.Deserialize(property as sfDictionaryProperty);
            terrainData.treePrototypes = prototypes;
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Applies server terrain data to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   TerrainType type
         * @param   sfBaseProperty prop - server data
         */
        private void ApplyServerTerrainData(TerrainData terrainData, TerrainType type, sfBaseProperty prop)
        {
            ApplyServerTerrainData(terrainData, type, prop, new RectInt(0, 0, 0, 0));
        }

        /**
         * Applies server terrain data to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   TerrainType type
         * @param   RectInt excludeBounds - exclude regions that overlap these bounds. If width or height is zero or
         *          less, all regions are updated.
         */
        public void ApplyServerTerrainData(TerrainData terrainData, TerrainType type, RectInt excludeBounds)
        {
            sfBaseProperty property = null;
            sfObject obj = sfObjectMap.Get().GetSFObject(terrainData);
            if (obj == null)
            {
                return;
            }
            sfDictionaryProperty dict = (sfDictionaryProperty)obj.Property;
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    property = dict[sfProp.Heightmap];
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    property = dict[sfProp.Alphamap];
                    break;
                }
                case TerrainType.HOLES:
                {
                    property = dict[sfProp.Holes];
                    break;
                }
                case TerrainType.DETAILS:
                {
                    property = dict[sfProp.Details];
                    break;
                }
            }
            if (property != null)
            {
                ApplyServerTerrainData(terrainData, type, property, excludeBounds);
            }
        }

        /**
         * Applies server terrain data to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   TerrainType type
         * @param   sfBaseProperty prop - server data
         * @param   RectInt excludeBounds - exclude regions that overlap these bounds. If width or height is zero or
         *          less, all regions are updated.
         */
        private void ApplyServerTerrainData(TerrainData terrainData, TerrainType type, sfBaseProperty prop, RectInt excludeBounds)
        {
            sfListProperty regionProps = prop as sfListProperty;
            if (regionProps == null)
            {
                return;
            }

            ForEachCallback callback = null;
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        ApplyServerHeightmapRegionData(terrainData, regionProps[regionIndex], region);
                    };
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        ApplyServerAlphamapRegionData(terrainData, regionProps[regionIndex], region);
                    };
                    break;
                }
                case TerrainType.HOLES:
                {
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        ApplyServerHolesRegionData(terrainData, regionProps[regionIndex], region);
                    };
                    break;
                }
                case TerrainType.DETAILS:
                {
                    sfListProperty layerProps = regionProps;
                    callback = delegate (RectInt region, int regionIndex)
                    {
                        for (int i = 0; i < layerProps.Count; i++)
                        {
                            regionProps = layerProps[i] as sfListProperty;
                            ApplyServerDetailRegionData(terrainData, regionProps[regionIndex], region, i);
                        }
                    };
                    break;
                }
            }
            if (excludeBounds.width > 0 && excludeBounds.height > 0)
            {
                // Exclude regions that overlap the bounds.
                ForEachCallback innerCallback = callback;
                callback = (RectInt region, int regionIndex) =>
                {
                    if (!region.Overlaps(excludeBounds))
                    {
                        innerCallback(region, regionIndex);
                    }
                };
            }
            ForEachTerrainRegion(terrainData, type, callback);
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply server details data to the terrain.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of detail layer data
         */
        private void ApplyServerDetails(TerrainData terrainData, sfListProperty property)
        {
            for (int i=0; i<property.Count; ++i)
            {
                sfListProperty regionProps = property[i] as sfListProperty;
                ApplyServerDetailLayer(terrainData, regionProps, i);
            }
        }

        /**
         * Apply server detail layer data to the terrain.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of detail regions data for a specific detail layer
         * @param   int - Detail layer to update
         */
        private void ApplyServerDetailLayer(TerrainData terrainData, sfListProperty property, int layer)
        {
            ForEachTerrainRegion(terrainData, TerrainType.DETAILS, (RectInt region, int regionIndex) =>
            {
                ApplyServerDetailRegionData(terrainData, property[regionIndex], region, layer);
            });
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Apply server tree data to the terrain.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfListProperty - List of tree data
         */
        private void ApplyServerTrees(TerrainData terrainData, sfListProperty property)
        {
            TreeInstance[] trees = new TreeInstance[(int)property[0]];
            for (int i = 1; i < property.Count; ++i)
            {
                byte[] data = m_compressor.DecodeTrees(((sfValueProperty)property[i]).Value.Data);
                m_treeInstanceSync.Deserialize(trees, data, (i-1) * TREE_GROUP_SIZE);
            }
            terrainData.SetTreeInstances(trees, true);
            sfUI.Get().MarkSceneViewStale();
        }

        /**
         * Applies server heightmap data in the given region to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   sfBaseProperty prop - heightmap data
         * @param   RectInt region of heightmap
         */
        private void ApplyServerHeightmapRegionData(TerrainData terrainData, sfBaseProperty prop, RectInt region)
        {
            float[,] regionHeightmapData = m_compressor.DecodeHeightmap(((sfValueProperty)prop).Value.Data,
                region.width, region.height);
            terrainData.SetHeights(region.x, region.y, regionHeightmapData);
        }

        /**
         * Applies server alphamap data in the given region to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   sfBaseProperty prop - alphamap data
         * @param   RectInt region of alphamap
         */
        private void ApplyServerAlphamapRegionData(TerrainData terrainData, sfBaseProperty prop, RectInt region)
        {
            float[,,] regionAlphamapData = m_compressor.DecodeAlphamap(((sfValueProperty)prop).Value.Data,
                region.width, region.height, terrainData.alphamapLayers);
            terrainData.SetAlphamaps(region.x, region.y, regionAlphamapData);
        }

        /**
         * Applies server holes data in the given region to the terrain.
         * 
         * @param   TerrainData terrainData to set
         * @param   sfBaseProperty prop - holes data
         * @param   RectInt region of holes
         */
        private void ApplyServerHolesRegionData(TerrainData terrainData, sfBaseProperty prop, RectInt region)
        {
            List<bool> decodedData = sfRunLength<bool>.Decode(((sfValueProperty)prop).Value.Data);
            bool[,] regionHolesData = new bool[region.height, region.width];
            Buffer.BlockCopy(decodedData.ToArray(), 0, regionHolesData, 0, decodedData.Count);
            terrainData.SetHoles(region.x, region.y, regionHolesData);
        }

        /**
         * Applies server detail data in the given region to the terrain.
         * 
         * @param   TerrainData - Terrain data to update
         * @param   sfBaseProperty - Detail data for a specific region
         * @param   RectInt - Region of terrain to update
         * @param   int - Detail layer to update
         */
        private void ApplyServerDetailRegionData(TerrainData terrainData,sfBaseProperty prop, RectInt region, int layer)
        {
            byte[] data = ((sfValueProperty)prop).Value.Data;
            int[,] regionDetailData = m_compressor.DecodeDetailLayer(data, region.width, region.height);
            terrainData.SetDetailLayer(region.x, region.y, layer, regionDetailData);
        }

        /**
         * Sends terrain change of the given type to server.
         * 
         * @param   TerrainType type of terrain data
         */
        public void SendTerrainChanges(TerrainType type)
        {
            Dictionary<TerrainData, HashSet<int>> dirtyTerrainRegions = null;
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    dirtyTerrainRegions = m_dirtyHeightmapRegions;
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    dirtyTerrainRegions = m_dirtyAlphamapRegions;
                    break;
                }
                case TerrainType.HOLES:
                {
                    dirtyTerrainRegions = m_dirtyHolesRegions;
                    break;
                }
                case TerrainType.TREES:
                {
                    SendTreeChanges();
                    return;
                }
                case TerrainType.DETAILS:
                {
                    SendDetailChanges();
                    return;
                }
            }
            if (dirtyTerrainRegions == null)
            {
                return;
            }

            foreach (KeyValuePair<TerrainData, HashSet<int>> pair in dirtyTerrainRegions)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }

                TerrainData terrainData = pair.Key;
                sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
                if (terrainDataObj == null)
                {
                    continue;
                }

                sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
                sfListProperty prop = null;
                ForEachCallback callback = null;
                switch (type)
                {
                    case TerrainType.HEIGHTMAP:
                    {
                        // If the heightmap resolution was updated, then the entire heightmap will
                        // be resynced and we can clear all dirty heightmap regions and continue with
                        // the next dirty terrain.
                        if (UpdateHeightmapResolution(terrainData))
                        {
                            pair.Value.Clear();
                            continue;
                        }
                        prop = dict[sfProp.Heightmap] as sfListProperty;
                        callback = delegate (RectInt rectRegion, int regionIndex)
                        {
                            prop[regionIndex] = SerializeHeightmapRegion(terrainData, rectRegion);
                        };
                        break;
                    }
                    case TerrainType.ALPHAMAP:
                    {
                        CheckTerrainLayers(terrainData);
                        // If the alphamap resolution was updated, then the entire alphamap will
                        // be resynced and we can clear all dirty alphamap regions and continue with
                        // the next dirty terrain.
                        if (UpdateAlphamapResolution(terrainData))
                        {
                            pair.Value.Clear();
                            continue;
                        }
                        prop = dict[sfProp.Alphamap] as sfListProperty;
                        callback = delegate (RectInt rectRegion, int regionIndex)
                        {
                            prop[regionIndex] = SerializeAlphamapRegion(terrainData, rectRegion);
                        };
                        break;
                    }
                    case TerrainType.HOLES:
                    {
                        // If the heightmap resolution was updated, then the hole data will
                        // be resynced and we can clear all dirty hole regions and continue with
                        // the next dirty terrain.
                        if (UpdateHeightmapResolution(terrainData))
                        {
                            pair.Value.Clear();
                            continue;
                        }
                        prop = dict[sfProp.Holes] as sfListProperty;
                        callback = delegate (RectInt rectRegion, int regionIndex)
                        {
                            prop[regionIndex] = SerializeHolesRegion(terrainData, rectRegion);
                        };
                        break;
                    }
                }
                if (prop == null)
                {
                    continue;
                }

                if (callback != null)
                {
                    int columnCount = GetResolution(terrainData, type) / REGION_RESOLUTION;
                    int resolution = GetResolution(terrainData, type);
                    foreach (int index in pair.Value)
                    {
                        callback(GetRegion(index, resolution), index);
                    }
                }
                pair.Value.Clear();
            }
        }

        /**
         * Send detail changes to the server.
         */
        public void SendDetailChanges()
        {
            // Get/Create the dirty regions for the terrain
            foreach (KeyValuePair<TerrainData, Dictionary<int, HashSet<int>>> terrainPair in m_dirtyDetailRegions)
            {
                if (terrainPair.Value == null)
                {
                    return;
                }

                TerrainData terrainData = terrainPair.Key;
                sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
                if (terrainDataObj == null)
                {
                    terrainPair.Value.Clear();
                    continue;
                }
                CheckDetailPrototypes(terrainData);

                int resolution = GetResolution(terrainData, TerrainType.DETAILS);
                sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
                sfListProperty layerListProp = dict[sfProp.Details] as sfListProperty;
                foreach (KeyValuePair<int, HashSet<int>> layerPair in terrainPair.Value)
                {
                    sfListProperty regionListProp = layerListProp[layerPair.Key] as sfListProperty;
                    foreach (int index in layerPair.Value)
                    {
                        RectInt region = GetRegion(index, resolution);
                        regionListProp[index] = SerializeDetailRegion(terrainData, region, layerPair.Key);
                    }
                }

                terrainPair.Value.Clear();
            }
        }

        /**
         * Send tree changes to the server.
         */
        private void SendTreeChanges()
        {
            foreach (KeyValuePair<TerrainData, bool> pair in m_dirtyTrees)
            {
                TerrainData terrainData = pair.Key;
                sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
                if (terrainDataObj == null)
                {
                    continue;
                }
                CheckTreePrototypes(terrainData);

                // Value is true when trees have been removed.
                if (pair.Value)
                {
                    // We don't know which trees were updated so we need to resync the entire tree list.
                    sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
                    dict[sfProp.Trees] = SerializeTrees(terrainData);
                }
                else
                {
                    SendAddedTrees(terrainData, terrainDataObj);
                }
            }
            m_dirtyTrees.Clear();
        }

        /**
         * Sends data for trees that were added to the server.
         * 
         * @param   TerrainData terrainData to send added trees for.
         * @param   sfObject terrainDatObj
         */
        private void SendAddedTrees(TerrainData terrainData, sfObject terrainDataObj)
        {
            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfListProperty treesProp = dict[sfProp.Trees] as sfListProperty;

            if ((int)treesProp[0] >= terrainData.treeInstanceCount)
            {
                return;
            }

            // Remove the last tree group. This group will always be reserialized
            if ((treesProp.Count - 1) > 0)
            {
                treesProp.RemoveAt(treesProp.Count - 1);
            }

            TreeInstance[] trees = terrainData.treeInstances;
            treesProp[0] = new sfValueProperty(trees.Length);
            int i = (treesProp.Count - 1) * TREE_GROUP_SIZE;
            for (; i < trees.Length; i += TREE_GROUP_SIZE)
            {
                int count = Math.Min(TREE_GROUP_SIZE, trees.Length - i);
                if (count > 0)
                {
                    byte[] data = m_treeInstanceSync.Serialize(trees, i, count);
                    treesProp.Add(new sfValueProperty(m_compressor.EncodeTrees(data)));
                }
            }
        }

        /**
         * Called when the terrain heightmap changed.
         * 
         * @param   Terrain terrain - the Terrain object that references a changed TerrainData asset.
         * @param   RectInt changeArea - the area that the heightmap changed.
         * @param   bool synced - indicates whether the changes were fully synchronized back to CPU memory.
         */
        private void OnHeightmapChange(Terrain terrain, RectInt changeArea, bool synced)
        {
            OnTerrainChange(terrain.terrainData, TerrainType.HEIGHTMAP, changeArea, synced);
        }

        /**
         * Called when the terrain textures changed.
         * 
         * @param   Terrain terrain - the Terrain object that references a changed TerrainData asset.
         * @param   string textureName - the name of the texture that changed.
         * @param   RectInt changeArea - the region of the Terrain texture that changed, in texel coordinates.
         * @param   bool synced - indicates whether the changes were fully synchronized back to CPU memory.
         */
        private void OnTextureChange(Terrain terrain, string textureName, RectInt changeArea, bool synced)
        {
            if (terrain != null)
            {
                if (textureName == "alphamap")
                {
                    OnTerrainChange(terrain.terrainData, TerrainType.ALPHAMAP, changeArea, synced);
                }
                else if (textureName == "holes")
                {
                    OnTerrainChange(terrain.terrainData, TerrainType.HOLES, changeArea, synced);
                }
            }
        }

        /**
         * Track a detail changes for a specific detail layer and area.
         * 
         * @param   TerrainData - Terrain data which has the changes
         * @param   RectInt - The area containing the detail changes
         * @param   int - The layer containing the detail changes
         */
        public void OnDetailChange(TerrainData terrainData, RectInt changeArea, int layer)
        {
            if (terrainData == null || layer >= terrainData.detailPrototypes.Length)
            {
                return;
            }

            sfUndoManager.Get().Record(new sfUndoTerrainOperation(terrainData, TerrainType.DETAILS, changeArea));

            // Get/Create the dirty regions for the terrain
            Dictionary<int, HashSet<int>> dirtyDetailRegions = null;
            if (!m_dirtyDetailRegions.TryGetValue(terrainData, out dirtyDetailRegions))
            {
                dirtyDetailRegions = new Dictionary<int, HashSet<int>>();
                m_dirtyDetailRegions[terrainData] = dirtyDetailRegions;
            }

            for (int i=0; i< terrainData.detailPrototypes.Length; ++i)
            {
                // If the layer is negative, then all layers need to be updated for the changed area.
                if (layer == i || layer < 0)
                {
                    // Get/Create the dirty regions for the detail layer of the terrain
                    HashSet<int> dirtyLayerRegions = null;
                    if (!dirtyDetailRegions.TryGetValue(i, out dirtyLayerRegions))
                    {
                        dirtyLayerRegions = new HashSet<int>();
                        dirtyDetailRegions[i] = dirtyLayerRegions;
                    }

                    // Add region to the dirty region hashset.
                    // TODO: rather than iterating all terrain regions checking for overlaps, we could generate
                    // a list of indices from the changedArea instead.
                    ForEachTerrainRegion(
                        terrainData,
                        TerrainType.DETAILS,
                        delegate (RectInt region, int regionIndex)
                        {
                            if (region.Overlaps(changeArea))
                            {
                                dirtyLayerRegions.Add(regionIndex);
                            }
                        }
                    );
                }
            }
        }

        /**
         * Called when tree data is changed.
         * 
         * @param   TerrainData terrainData
         * @param   bool hasRemovals - true if trees were removed.
         */
        public void OnTreeChange(TerrainData terrainData, bool hasRemovals)
        {
            sfUndoManager.Get().Record(new sfUndoTerrainTreeOperation(terrainData));
            bool currentHasRemovals;
            if (!m_dirtyTrees.TryGetValue(terrainData, out currentHasRemovals) || !currentHasRemovals)
            {
                m_dirtyTrees[terrainData] = hasRemovals;
            }
        }

        /**
         * Check terrain data properties which do not have Unity events.
         * 
         * @param   TerrainData - Terrain data to check for changes.
         */
        private void CheckTerrain(TerrainData terrainData)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(terrainData);
            if (obj == null)
            {
                return;
            }

            CheckTerrainLayers(terrainData);
            CheckDetailPrototypes(terrainData);
            CheckTreePrototypes(terrainData);
            bool resendDetails = false;
            sfDictionaryProperty dict = (sfDictionaryProperty)obj.Property;
            foreach (KeyValuePair<string, PropertyGetter> getter in m_getters)
            {
                sfBaseProperty property = getter.Value(terrainData);
                sfBaseProperty current = dict[getter.Key];
                if (!property.Equals(current))
                {
                    if (!sfPropertyManager.Get().Copy(dict[getter.Key], property))
                    {
                        dict[getter.Key] = property;
                    }
                    if (m_sendDetailsOnChange.Contains(getter.Key))
                    {
                        resendDetails = true;
                    }
                }
            }
            if (resendDetails)
            {
                // Clear dirty region checks since we are resyncing the entire detail map.
                Dictionary<int, HashSet<int>> dirtyDetailRegions;
                if (m_dirtyDetailRegions.TryGetValue(terrainData, out dirtyDetailRegions))
                {
                    dirtyDetailRegions.Clear();
                }
                dict[sfProp.Details] = SerializeDetails(terrainData);
            }
        }

        /**
         * Check Terrain palette for additions, removals, and updates.
         * 
         * @param   TerrainData - Terrain data to check
         */
        private void CheckTerrainLayers(TerrainData terrainData)
        {
            sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
            if (terrainDataObj == null)
            {
                return;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfListProperty list = SerializeTerrainLayerPalette(terrainData);
            sfPropertyManager.Get().Copy(dict[sfProp.TerrainLayerPalette], list);
        }

        /**
         * Check detail prototypes for additions, removals, and updates.
         * 
         * @param   TerrainData - Terrain data to check
         */
        private void CheckDetailPrototypes(TerrainData terrainData)
        {
            sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
            if (terrainDataObj == null)
            {
                return;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfListProperty list = SerializeDetailPrototypePalette(terrainData);
            sfListProperty detailProps = dict[sfProp.Details] as sfListProperty;
            sfPropertyManager.Get().CopyList((sfListProperty)dict[sfProp.DetailPrototypePalette], list,
                (bool isInsertion, int index, int count) => // Handle added/removed prototype(s)
                {
                    if (isInsertion)
                    {
                        // Serialize the added detail layers.
                        for (int i = index; i < index + count; i++)
                        {
                            sfListProperty regionProps = new sfListProperty();
                            ForEachTerrainRegion(terrainData, TerrainType.DETAILS, (RectInt region, int regionIndex) =>
                            {
                                regionProps.Add(SerializeDetailRegion(terrainData, region, i));
                            });
                            detailProps.Insert(i, regionProps);
                        }
                    }
                    else
                    {
                        detailProps.RemoveRange(index, count);
                    }
                });
        }

        /**
         * Check tree prototypes for additions, removals, and updates.
         * 
         * @param   TerrainData - Terrain data to check
         */
        private void CheckTreePrototypes(TerrainData terrainData)
        {
            sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
            if (terrainDataObj == null)
            {
                return;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfListProperty list = dict[sfProp.TreePrototypePalette] as sfListProperty;

            TreePrototype[] prototypes = terrainData.treePrototypes;
            bool updated = false;
            int i = 0;
            for (; i < prototypes.Length; i++)
            {
                if (i >= list.Count)
                {
                    // Add new prototype property
                    list.Add(m_treePrototypeSync.Serialize(prototypes[i], ref updated));
                    continue;
                }
                else
                {
                    // Update prototype property
                    updated |= m_treePrototypeSync.UpdateProperties(list[i] as sfDictionaryProperty, prototypes[i]);
                }
            }

            // Remove excess prototypes
            if (i < list.Count)
            {
                list.RemoveRange(i, list.Count - i);
            }

            if (updated)
            {
                terrainData.treePrototypes = prototypes;
            }
        }

        /**
         * Called when the terrain changed.
         * 
         * @param   TerrainData terrainData that changed.
         * @param   TerrainType type of change
         * @param   RectInt changeArea - the region that changed.
         * @param   bool synced - indicates whether the changes were fully synchronized back to CPU memory.
         */
        public void OnTerrainChange(TerrainData terrainData, TerrainType type, RectInt changeArea, bool synced)
        {
            if (!synced || terrainData == null || m_applyingServerUpdates.Contains(terrainData))
            {
                return;
            }
            sfUndoManager.Get().Record(new sfUndoTerrainOperation(terrainData, type, changeArea));
            HashSet<int> dirtyRegionIndexes = GetDirtyRegionIndexes(terrainData, type);
            ForEachTerrainRegion(
                terrainData,
                type,
                delegate (RectInt region, int regionIndex)
                {
                    if (region.Overlaps(changeArea))
                    {
                        dirtyRegionIndexes.Add(regionIndex);
                    }
                });
        }

        /**
         * Gets the resolution of the terrain type in the given terrain data.
         * 
         * @param   TerrainData terrainData to get resolution from
         * @param   TerrainType type of resolution to get
         * @return  int
         */
        private int GetResolution(TerrainData terrainData, TerrainType type)
        {
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    return terrainData.heightmapResolution;
                }
                case TerrainType.ALPHAMAP:
                {
                    return terrainData.alphamapResolution;
                }
                case TerrainType.HOLES:
                {
                    return terrainData.holesResolution;
                }
                case TerrainType.DETAILS:
                {
                    return terrainData.detailResolution;
                }
                default:
                {
                    ksLog.Error(this, "Cannot get resolution for terrain type " + type);
                    return 0;
                }
            }
        }

        /**
         * Gets the dirty region indexes of the terrain type in the given terrain data.
         * 
         * @param   TerrainData terrainData to get resolution from
         * @param   TerrainType type of resolution to get
         * @return  HashSet<int> - dirty region indexes
         */
        private HashSet<int> GetDirtyRegionIndexes(TerrainData terrainData, TerrainType type)
        {
            HashSet<int> dirtyRegionIndexes = null;
            switch (type)
            {
                case TerrainType.HEIGHTMAP:
                {
                    if (!m_dirtyHeightmapRegions.TryGetValue(terrainData, out dirtyRegionIndexes))
                    {
                        dirtyRegionIndexes = new HashSet<int>();
                        m_dirtyHeightmapRegions[terrainData] = dirtyRegionIndexes;
                    }
                    break;
                }
                case TerrainType.ALPHAMAP:
                {
                    if (!m_dirtyAlphamapRegions.TryGetValue(terrainData, out dirtyRegionIndexes))
                    {
                        dirtyRegionIndexes = new HashSet<int>();
                        m_dirtyAlphamapRegions[terrainData] = dirtyRegionIndexes;
                    }
                    break;
                }
                case TerrainType.HOLES:
                {
                    if (!m_dirtyHolesRegions.TryGetValue(terrainData, out dirtyRegionIndexes))
                    {
                        dirtyRegionIndexes = new HashSet<int>();
                        m_dirtyHolesRegions[terrainData] = dirtyRegionIndexes;
                    }
                    break;
                }
                default:
                {
                    ksLog.Error(this, "Cannot get dirty regions for terrain type " + type);
                    break;
                }
            }
            return dirtyRegionIndexes;
        }

        /**
         * Calls a callback on all terrain regions.
         * 
         * @param   TerrainData terrainData
         * @param   TerrainType type
         * @param   ForEachCallback callback to call.
         */
        private void ForEachTerrainRegion(TerrainData terrainData, TerrainType type, ForEachCallback callback)
        {
            int resolution = GetResolution(terrainData, type);
            if (resolution <= 0)
            {
                return;
            }
            int columnCount = GetNumColumns(resolution);
            int regionsCount = columnCount * columnCount;
            for (int i = 0; i < regionsCount; i++)
            {
                callback(GetRegion(i, resolution), i);
            }
        }

        /**
         * Iterates the synced terrain components that reference the given terrain data.
         * 
         * @param   TerrainData terrainData. Terrains that reference this terrain data will be iterated.
         * @return  IEnumerable<Terrain>
         */
        private IEnumerable<Terrain> IterateTerrainComponents(TerrainData terrainData)
        {
            for (int i = m_terrains.Count - 1; i >= 0; i--)
            {
                Terrain terrain = m_terrains[i];
                if (terrain == null)
                {
                    m_terrains.RemoveAt(i);
                }
                else if (terrain.terrainData == terrainData)
                {
                    yield return terrain;
                }
            }
        }

        private int GetNumColumns(int resolution)
        {
            // We subtract 2 instead of 1 from the resolution because the heightmap is always a power of two plus 1 so
            // we allow the last region to be 1 size larger than the REGION_RESOLUTION instead of having a region with
            // a size of 1 at the end.
            return 1 + (resolution - 2) / REGION_RESOLUTION;
        }

        private RectInt GetRegion(int index, int resolution)
        {
            int columnCount = GetNumColumns(resolution);
            int x = index % columnCount;
            int y = index / columnCount;
            RectInt region = new RectInt(x * REGION_RESOLUTION, y * REGION_RESOLUTION, 0, 0);
            region.width = x == columnCount - 1 ? resolution - region.x : REGION_RESOLUTION;
            region.height = y == columnCount - 1 ? resolution - region.y : REGION_RESOLUTION;
            return region;
        }

        /**
         * Check if the heightmap resolution has changed. If it changed, then
         * resync all of the hieghtmap and hole data.
         *
         * @param   TerrainData - Terrain data
         * @return  bool - True if the hightmap resolution changed.
         */
        private bool UpdateHeightmapResolution(TerrainData terrainData)
        {
            sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
            if (terrainDataObj == null)
            {
                return false;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfValueProperty heightmapResolution = dict[sfProp.HeightmapResolution] as sfValueProperty;
            if ((int)heightmapResolution != terrainData.heightmapResolution)
            {
                heightmapResolution.Value = terrainData.heightmapResolution;
                dict[sfProp.Heightmap] = SerializeTerrainData(terrainData, TerrainType.HEIGHTMAP);
                dict[sfProp.Holes] = SerializeTerrainData(terrainData, TerrainType.HOLES);
                return true;
            }
            return false;
        }

        /**
         * Check if the alphamap resolution has changed. If it changed, then
         * resync all of the alphamap data.
         *
         * @param   TerrainData - Terrain data
         * @return  bool - True if the alphamap resolution changed.
         */
        private bool UpdateAlphamapResolution(TerrainData terrainData)
        {
            sfObject terrainDataObj = sfObjectMap.Get().GetSFObject(terrainData);
            if (terrainDataObj == null)
            {
                return false;
            }

            sfDictionaryProperty dict = (sfDictionaryProperty)terrainDataObj.Property;
            sfValueProperty alphamapResolution = dict[sfProp.AlphamapResolution] as sfValueProperty;
            if (alphamapResolution.Value.Int != terrainData.alphamapResolution)
            {
                alphamapResolution.Value = terrainData.alphamapResolution;
                dict[sfProp.Alphamap] = SerializeTerrainData(terrainData, TerrainType.ALPHAMAP);
                return true;
            }
            return false;
        }
    }
}
