using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UIElements;
using UnityEngine.Experimental.Rendering;
using UnityEditor;
using UnityEditor.Presets;
using KS.Reactor;
using KS.Unity.Editor;
using KS.SceneFusion;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Loads and caches assets.
     */
    internal class sfLoader
    {
        /**
         * Singleton instance
         */
        public static sfLoader Get()
        {
            return m_instance;
        }
        private static sfLoader m_instance = new sfLoader();

        /**
         * Load error event handler.
         * 
         * @param   sfAssetInfo assetInfo that failed to load.
         * @param   string message
         */
        public delegate void LoadErrorHandler(sfAssetInfo assetInfo, string message);

        /**
         * Invoked when an asset cannot be loaded.
         */
        public LoadErrorHandler OnLoadError;

        /**
         * Find missing asset event handler.
         * 
         * @param   sfAssetInfo assetInfo for the asset that is no longer missing.
         * @param   UObject asset that used to be missing.
         */
        public delegate void FindMissingAssetHandler(sfAssetInfo assetInfo, UObject asset);

        /**
         * Invoked when an asset that was missing is created.
         */
        public FindMissingAssetHandler OnFindMissingAsset;

        /**
         * New asset event handler.
         * 
         * @param   sfAssetInfo assetInfo for the new asset.
         * @param   UObject asset that was created.
         */
        public delegate void NewAssetHandler(sfAssetInfo assetInfo, UObject asset);

        /**
         * Invoked when an asset is added to the cache.
         */
        public NewAssetHandler OnCacheAsset;

        /**
         * Generator for an asset.
         * 
         * @return  UObject generated asset.
         */
        public delegate UObject Generator();

        /**
         * Callback to parse and possibly change a line of text in a file.
         * 
         * @param   ref string line of text
         */
        private delegate void LineParser(ref string line);
        
        private Dictionary<sfAssetInfo, UObject> m_cache = new Dictionary<sfAssetInfo, UObject>();
        private Dictionary<UObject, sfAssetInfo> m_infoCache = new Dictionary<UObject, sfAssetInfo>();
        // Syncable types are keys. If the generator is null, we call ScriptableObject.CreateInstance or attempt to
        // call the default constructor.
        private Dictionary<Type, Generator> m_generators = new Dictionary<Type, Generator>();
        private Dictionary<Type, Generator> m_standInGenerators = new Dictionary<Type, Generator>();
        // When we need to create a stand-in instance, we first check for a template asset in this map to copy, and if
        // there isn't one we check for a stand-in generator.
        private Dictionary<Type, UObject> m_standInTemplates = new Dictionary<Type, UObject>();
        // When we need to create a temporary library asset and set its GUID, we first check if there is an asset for
        // that type to copy in the library overrides, and then we check m_standInTemplates. This is for when we want
        // to use a different template for the library asset than we use for the stand-in instance. See comments in
        // LoadStandInTemplates for details.
        private Dictionary<Type, UObject> m_standInTemplateLibraryOverrides = new Dictionary<Type, UObject>();
        private Dictionary<UObject, UObject> m_replacements = new Dictionary<UObject, UObject>();
        private HashSet<UObject> m_standInInstances = new HashSet<UObject>();
        private HashSet<UObject> m_createdAssets = new HashSet<UObject>();

        // Built-in assets will use their name with this prefix as their path.
        public const string BUILT_IN_PREFIX = "BuiltIn/";

        /**
         * Singleton constructor
         */
        private sfLoader()
        {
            LoadGenerators();
            LoadStandInTemplates();
        }

        /**
         * Constructor that sets the info cache for testing.
         * 
         * @param   Dictionary<UObject, sfAssetInfo> map of UObjects to asset info.
         */
        internal sfLoader(Dictionary<UObject, sfAssetInfo> infoCache)
        {
            m_infoCache = infoCache;
        }

        /**
         * Loads syncable asset generators and stand-in generators.
         */
        private void LoadGenerators()
        {
            m_generators[typeof(TerrainData)] = () => new TerrainData();
            m_generators[typeof(TerrainLayer)] = () => new TerrainLayer();
            m_generators[typeof(LightingSettings)] = () => new LightingSettings();

            m_standInGenerators[typeof(SparseTexture)] = () => new SparseTexture(1, 1, TextureFormat.Alpha8, 1);
            m_standInGenerators[typeof(CustomRenderTexture)] = () => new CustomRenderTexture(1, 1);
            m_standInGenerators[typeof(RenderTexture)] = () => new RenderTexture(1, 1, 1);
            m_standInGenerators[typeof(CubemapArray)] = () => new CubemapArray(1, 1, TextureFormat.Alpha8, false);
            m_standInGenerators[typeof(Preset)] = () => new Preset(m_standInTemplates[typeof(GameObject)]);

            // Video files can only be imported on Windows if QuickTime is installed. Otherwise an error is logged when
            // Unity tries to import the video. To avoid the error we store the asset as a binary file and change the
            // file extension and reimport it when it's needed.
            m_standInGenerators[typeof(VideoClip)] = delegate ()
            {
                string path = sfPaths.PackageRoot + "Stand-Ins/BlackFrame";
                string oldPath = path + ".bin";
                string newPath = path + ".avi";
                if (RenameFile(oldPath, newPath) && File.Exists(newPath))
                {
                    VideoClip asset = AssetDatabase.LoadAssetAtPath<VideoClip>(newPath);
                    if (asset == null)
                    {
                        ksLog.Error(this, "Unable to load VideoClip '" + newPath + "'.");
                    }
                    else
                    {
                        m_standInTemplates[typeof(VideoClip)] = asset;
                        return UObject.Instantiate(asset);
                    }
                }
                return null;
            };
        }

        /**
         * Renames a file and deletes the meta file for it. Returns true if the file was successfully renamed.
         * 
         * @param   string oldPath
         * @param   string newPath
         * @return  bool
         */
        private bool RenameFile(string oldPath, string newPath)
        {
            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                try
                {
                    File.Move(oldPath, newPath);
                }
                catch (Exception e)
                {
                    ksLog.Error(this, "Error moving '" + oldPath + "' to '" + newPath + "'.", e);
                    return false;
                }
                string oldMetaFilePath = oldPath + ".meta";
                try
                {
                    if (File.Exists(oldMetaFilePath))
                    {
                        File.Delete(oldMetaFilePath);
                    }
                }
                catch (Exception e)
                {
                    ksLog.Error(this, "Error deleting '" + oldMetaFilePath + "'.", e);
                }
                AssetDatabase.Refresh();
                return true;
            }
            return false;
        }

        /**
         * Loads built-in and stand-in assets.
         */
        public void Initialize()
        {
            LoadBuiltInAssets();
            ksEditorEvents.OnImportAssets += HandleImportAssets;
        }

        /**
         * Destroys stand-in instances and clears the cache.
         */
        public void CleanUp()
        {
            foreach (UObject standIn in m_standInInstances)
            {
                UObject.DestroyImmediate(standIn);
            }
            m_createdAssets.Clear();
            m_standInInstances.Clear();
            m_cache.Clear();
            m_infoCache.Clear();
            m_replacements.Clear();
            ksEditorEvents.OnImportAssets -= HandleImportAssets;
        }

        /**
         * Checks if we can create a stand-in of the given type.
         * 
         * @param   Type type to check.
         * @return  bool true if we can create a stand-in for the type.
         */
        public bool CanCreateStandIn(Type type)
        {
            return m_standInTemplates.ContainsKey(type) || m_standInGenerators.ContainsKey(type) ||
                typeof(ScriptableObject).IsAssignableFrom(type) || !new ksReflectionObject(type).GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, true).IsVoid;
        }

        /**
         * Checks if a Unity object is an asset or asset stand-in.
         * 
         * @param   UObject obj to check.
         * @return  bool true if the object is an asset or asset stand-in.
         */
        public bool IsAsset(UObject obj)
        {
            return obj != null && (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)) || IsStandIn(obj));
        }

        /**
         * Checks if an object is a built-in asset.
         * 
         * @param   UObject uobj to check.
         * @return  bool true if the uobject is a built-in asset.
         */
        public bool IsBuiltInAsset(UObject uobj)
        {
            return uobj != null && IsBuiltInAsset(AssetDatabase.GetAssetPath(uobj));
        }

        /**
         * Checks if an asset path is for a built-in asset.
         * 
         * @param   string path to check.
         * @return  bool true if the path is for a built-in asset.
         */
        public bool IsBuiltInAsset(string path)
        {
            return path == "Resources/unity_builtin_extra" || path == "Library/unity default resources";
        }

        /**
         * Checks if an object is a library asset. Library assets are processed and written to the library folder and
         * loaded from there. References to assets are saved with a "type" value that indicates if the asset is a
         * library asset (type 3) or not (type 2). If this type value is incorrect, Unity will log an unknown error
         * when it tries to load the asset.
         * 
         * @return  bool true if the asset is a library asset.
         */
        public bool IsLibraryAsset(UObject obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            // The asset is a library asset if its importer is not AssetImporter, or if it is a SceneAsset or
            // DefaultAsset and not a subsasset.
            return !string.IsNullOrEmpty(path) && (GetImporterType(path) != typeof(AssetImporter) ||
                ((obj is SceneAsset || obj is DefaultAsset) && !AssetDatabase.IsSubAsset(obj)));
        }

        /**
         * Gets the asset importer type for the asset at the given path.
         * 
         * @param   string path of asset to get importer type for.
         * @return  Type of importer for the asset.
         */
        private Type GetImporterType(string path)
        {
#if UNITY_2022_2_OR_NEWER
            return AssetDatabase.GetImporterType(path);
#else
            AssetImporter importer = AssetImporter.GetAtPath(path);
            return importer == null ? null : importer.GetType();
#endif
        }

        /**
         * Checks if an object is an asset stand-in.
         * 
         * @param   UObject obj to check.
         * @return  bool true if the object is an asset stand-in.
         */
        public bool IsStandIn(UObject obj)
        {
            return obj != null && m_standInInstances.Contains(obj);
        }

        /**
         * Was this asset created when we tried to load it?
         * 
         * @param   UObject obj to check.
         * @return  bool true if the object was created on load.
         */
        public bool WasCreatedOnLoad(UObject obj)
        {
            return obj != null && m_createdAssets.Contains(obj);
        }

        /**
         * Is this object a syncable asset type? These assets are created if they are not found during loading.
         * 
         * @param   UObject obj to check.
         * @return  bool true if the object is a syncable asset type.
         */
        public bool IsSyncableAssetType(UObject obj)
        {
            return obj != null && m_generators.ContainsKey(obj.GetType());
        }

        /**
         * Is this a type of asset that the loader will create if it is not found during loading?
         * 
         * @param   Type type to check.
         * @return  bool true if the type is a syncable asset type.
         */
        public bool IsSyncableAssetType(Type type)
        {
            return m_generators.ContainsKey(type);
        }

        /**
         * Registers an asset type as a syncable type. Scene Fusion will attempt to sync these assets if they are
         * referenced in the scene and will create the asset if it does not exist locally. Scene Fusion syncs the assets
         * serialized properties. This will not work for binary assets whose data is not available via serialized
         * properties.
         * 
         * @param   Generator generator - optional generator for creating an instance of the asset type if the asset
         *          was not found. If null, ScriptableObject.CreateInstance will be used for scriptable objects,
         *          and the default constructor will be called if it exists for UObjects. Non scriptable objects
         *          without a default constructor cannot be generated without a generator.
         */
        public void RegisterSyncableType<T>(Generator generator = null)
        {
            m_generators[typeof(T)] = generator;
        }

        /**
         * Gets the asset info for an asset used to load the object from the asset cache.
         * 
         * @param   UObject asset to get asset info for.
         * @return  sfAssetInfo asset info.
         */
        public sfAssetInfo GetAssetInfo(UObject asset)
        {
            if (asset == null)
            {
                return new sfAssetInfo();
            }
            sfAssetInfo info;
            if (!m_infoCache.TryGetValue(asset, out info))
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (IsBuiltInAsset(path))
                {
                    info = new sfAssetInfo();
                    info.Path = BUILT_IN_PREFIX + asset.name;
                    info.ClassName = asset.GetType().ToString();

                    // If there's already an asset cached for this path, it means we tried and failed to load this
                    // built-in asset before, so invoke the find missing asset event.
                    if (m_cache.ContainsKey(info) && OnFindMissingAsset != null)
                    {
                        OnFindMissingAsset(info, asset);
                    }
                    m_infoCache[asset] = info;
                    m_cache[info] = asset;
                }
                else if (!string.IsNullOrEmpty(path))
                {
                    info = CacheAssets(path, asset);
                }
            }
            return info;
        }

        /**
         * Creates a uobject of the given type.
         * 
         * @param   Type type
         * @return  UObject created uobject, or null if one could not be created.
         */
        public UObject Create(Type type)
        {
            Generator generator;
            if (m_generators.TryGetValue(type, out generator) && generator != null)
            {
                return generator();
            }
            return Construct(type);
        }

        /**
         * Loads an asset of type T. Tries first to load from the cache, and if it's not found, caches it. If the asset
         * is not found but is a syncable type, generates one.
         * 
         * @param   sfAssetInfo info of asset to load.
         * @param   string guid to assign the generated asset, if one is generated.
         * @return  T asset, or null if the asset was not found.
         */
        public T Load<T>(sfAssetInfo info, string guid = null) where T : UObject
        {
            return Load(info, guid) as T;
        }

        /**
         * Loads an asset. Tries first to load from the cache, and if it's not found, caches it. If the asset is not
         * found but is a syncable type, generates one.
         * 
         * @param   sfAssetInfo info of asset to load.
         * @param   string guid to assign the generated asset, if one is generated.
         * @return  UObject asset, or null if the asset was not found.
         */
        public UObject Load(sfAssetInfo info, string guid = null)
        {
            if (!info.IsValid)
            {
                return null;
            }
            UObject asset;
            if (!m_cache.TryGetValue(info, out asset) || asset.IsDestroyed())
            {
                Type type = sfTypeCache.Get().Load(info.ClassName);
                if (type == null)
                {
                    type = typeof(UObject);
                    ksLog.Warning(this, "Cannot determine type of asset " + info.Path + ". Trying " + type);
                }

                if (info.IsBuiltIn)
                {
                    // Use the slow fallback method to find and cache built-ins that can't be loaded normally.
                    FindUncachedBuiltInsSlow(type);
                    m_cache.TryGetValue(info, out asset);
                }
                else
                {
                    // Load and cache the assets at the path.
                    asset = CacheAssets(info.Path, info.Index);
                }

                if (asset == null)
                {
                    Generator generator;
                    if (info.Index == 0 && m_generators.TryGetValue(type, out generator) && !info.IsBuiltIn)
                    {
                        ksLog.Info(this, "Generating " + type + " '" + info.Path + "'.");
                        if (generator != null)
                        {
                            asset = generator();
                        }
                        else
                        {
                            asset = Construct(type);
                        }

                        if (asset != null && asset.GetType() == type)
                        {
                            ksPathUtils.Create(info.Path);
                            AssetDatabase.CreateAsset(asset, info.Path);
                            if (!string.IsNullOrEmpty(guid))
                            {
                                SetGuid(info.Path, guid);
                                // Reload the asset since changing it's guid makes it a different object.
                                AssetDatabase.Refresh();
                                asset = AssetDatabase.LoadAssetAtPath(info.Path, asset.GetType());
                            }

                            if (asset != null)
                            {
                                m_infoCache[asset] = info;
                                m_cache[info] = asset;
                                m_createdAssets.Add(asset);
                            }
                            else
                            {
                                ksLog.Warning(this, "Could not load generated " + info + ".");
                            }
                        }
                        else
                        {
                            ksLog.Warning(this, "Could not generate " + type + ".");
                        }
                    }
                    else
                    {
                        string message = "Unable to load " + info + ".";
                        if (OnLoadError != null)
                        {
                            OnLoadError(info, message);
                        }
                        else
                        {
                            ksLog.Error(this, message);
                        }
                    }
                }

                if (asset != null && asset.GetType() != type)
                {
                    string message = "Expected asset at '" + info.Path + "' index " + info.Index + " to be type " + type +
                        " but found " + asset.GetType();
                    if (OnLoadError != null)
                    {
                        OnLoadError(info, message);
                    }
                    else
                    {
                        ksLog.Error(this, message);
                    }
                    asset = null;
                }

                if (asset == null)
                {
                    Generator generator;
                    if (m_standInTemplates.TryGetValue(type, out asset))
                    {
                        asset = UObject.Instantiate(asset);
                    }
                    else if (m_standInGenerators.TryGetValue(type, out generator))
                    {
                        asset = generator();
                    }
                    else
                    {
                        asset = Construct(type);
                    }
                    if (asset != null)
                    {
                        m_infoCache[asset] = info;
                        asset.name = "Missing " + type.Name + " (" + info.Path + ")";
                        if (info.Index != 0)
                        {
                            asset.name += "[" + info.Index + "]";
                        }
                        asset.hideFlags = HideFlags.HideAndDontSave;
                        m_standInInstances.Add(asset);
                    }
                    else
                    {
                        ksLog.Warning(this, "Could not create " + type + " stand-in.");
                    }
                    m_cache[info] = asset;
                }
            }
            return asset;
        }

        /**
         * Constructs an instance of a UObject type. Returns null if the type could not be constructed.
         * 
         * @param   Type type to construct.
         * @retur   UObject instance of type, or null if it could not be constructed.
         */
        private UObject Construct(Type type)
        {
            if (typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return ScriptableObject.CreateInstance(type);
            }
            if (typeof(UObject).IsAssignableFrom(type))
            {
                try
                {
                    return (UObject)Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    // Ignore the exception; we'll log something later.
                }
            }
            return null;
        }

        /**
         * Gets the correct asset for a stand-in if it is available. This will not attempt to load the asset if it is
         * not already cached.
         * 
         * @param   T uobj to get replacement asset for.
         * @return  T replacement asset for the uobj, or null if none was found.
         */
        public T GetAssetForStandIn<T>(T uobj) where T : UObject
        {
            if (uobj == null)
            {
                return null;
            }
            UObject asset;
            m_replacements.TryGetValue(uobj, out asset);
            return asset as T;
        }

        /**
         * Creates an asset in a temp folder for a stand-in with a specific guid and optional file id.
         * 
         * @param   UObject standIn to create asset for.
         * @param   string guid
         * @param   bool isLibraryAsset - is the stand-in for a library asset? Library assets are processed and written
         *          to the library folder and loaded from there.
         * @param   long fileId - optional file id. File ids are used to identify subassets in non-library assets and
         *          prefabs. If the stand-in is not for a non-library subasset or prefab, leave this as zero.
         * @return  UObject asset created for the stand-in, or null if an asset could not be created.
         */
        public UObject CreateStandInAssetWithGuid(UObject standIn, string guid, bool isLibraryAsset, long fileId = 0)
        {
            if (!m_standInInstances.Contains(standIn))
            {
                return null;
            }
            sfAssetInfo info = GetAssetInfo(standIn);
            string path = sfPaths.Temp + "StandIn" + standIn.GetInstanceID();
            ksPathUtils.Create(path);

            if (isLibraryAsset)
            {
                GameObject standInGameObject = standIn as GameObject;
                if (standInGameObject != null)
                {
                    // Don't save hide flags must be removed to save the object as a prefab.
                    standInGameObject.hideFlags = HideFlags.HideInHierarchy;
                    path += ".prefab";
                    PrefabUtility.SaveAsPrefabAsset(standInGameObject, path);
                    standInGameObject.hideFlags = HideFlags.HideAndDontSave;
                    SetFileId(path, fileId);
                }
                else if (standIn is DefaultAsset)
                {
                    AssetDatabase.CreateFolder(ksPathUtils.Clean(sfPaths.Temp), "StandIn" + standIn.GetInstanceID());
                }
                else
                {
                    string templatePath = null;
                    UObject template;
                    if (m_standInTemplateLibraryOverrides.TryGetValue(standIn.GetType(), out template))
                    {
                        templatePath = AssetDatabase.GetAssetPath(template);
                    }
                    else if (m_standInTemplates.TryGetValue(standIn.GetType(), out template))
                    {
                        templatePath = AssetDatabase.GetAssetPath(template);
                    }
                    if (string.IsNullOrEmpty(templatePath))
                    {
                        return null;
                    }

                    int index = templatePath.LastIndexOf('.');
                    if (index >= 0)
                    {
                        path += templatePath.Substring(index);
                    }
                    try
                    {
                        File.Copy(templatePath, path);
                        File.Copy(templatePath + ".meta", path + ".meta");
                    }
                    catch (Exception e)
                    {
                        ksLog.Error(this, "Error copying " + templatePath + " to " + path + ".", e);
                    }
                }
                SetGuid(path, guid);
            }
            else
            {
                path += ".asset";
                standIn = UObject.Instantiate(standIn);
                standIn.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(standIn, path);
                // Setting the guid does not always change the stand-in's guid. Instead it sometimes makes the asset we
                // just created into a different object with a different guid.
                SetGuid(path, guid);
                if (fileId != 0 && info.IsSubasset)
                {
                    SetFileId(path, fileId);
                }
            }
            AssetDatabase.Refresh();
            // Load the asset we just created.
            UObject asset = AssetDatabase.LoadAssetAtPath(path, standIn.GetType());
            // If the real asset is a library asset and the stand-in is not, or vice versa, Unity will log an unknown
            // error when the real asset is available and it tries to load the reference to it, so we return null in
            // this case.
            if (asset == null || IsLibraryAsset(asset) != isLibraryAsset)
            {
                return null;
            }
            return asset;
        }

        /**
         * Sets the guid for an asset by parsing and updating the meta file for the asset.
         * 
         * @param   string path to the asset to set the guid for. This is not the path to the meta file.
         * @param   string guid to set.
         */
        private void SetGuid(string path, string guid)
        {
            UpdateFile(path + ".meta", (ref string line) =>
            {
                int index = 0;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                {
                    index++;
                }
                if (index >= line.Length)
                {
                    return;
                }
                int endIndex = line.IndexOf(':');
                if (endIndex <= index)
                {
                    return;
                }
                string paramName = line.Substring(index, endIndex - index);
                if (paramName == "guid")
                {
                    line = line.Substring(0, endIndex + 1) + " " + guid;
                }
            });
        }

        /**
         * Sets the file id of the first asset in an asset file by parsing and updating the asset file. Does not work
         * and logs a warning if serialization mode is not ForceText.
         * 
         * @param   string path to the asset to set the file id for.
         * @param   long fileId to set.
         */
        private void SetFileId(string path, long fileId)
        {
            if (EditorSettings.serializationMode != SerializationMode.ForceText)
            {
                ksLog.Warning(this, "Cannot set file id for '" + path + "' when serialization mode is not ForceText.");
                return;
            }
            string oldFileId = null;
            UpdateFile(path, (ref string line) =>
            {
                if (oldFileId != null)
                {
                    int index = line.IndexOf("{fileID: " + oldFileId + "}");
                    if (index >= 0)
                    {
                        line = line.Substring(0, index + 9) + fileId + line.Substring(index + 9 + oldFileId.Length);
                    }
                }
                else if (line.StartsWith("--- !u!"))
                {
                    int index = line.IndexOf('&');
                    if (index >= 0)
                    {
                        oldFileId = line.Substring(index + 1);
                        line = line.Substring(0, index + 1) + fileId;
                    }
                }
            });
        }

        /**
         * Parses and updates a file line by line.
         *
         * @param   string path to file to update.
         * @param   LineParser parser for parsing and updating each line.
         */
        private void UpdateFile(string path, LineParser parser)
        {
            // Write to a temp file.
            string tmpPath = path + ".tmp";
            try
            {
                using (StreamReader reader = new StreamReader(path))
                {
                    using (StreamWriter writer = new StreamWriter(tmpPath))
                    {
                        string line = reader.ReadLine();
                        while (line != null)
                        {
                            parser(ref line);
                            writer.WriteLine(line);
                            line = reader.ReadLine();
                        }
                    }
                }
                // Delete the old file and replace it with the temp file
                File.Delete(path);
                File.Move(tmpPath, path);
            }
            catch (Exception e)
            {
                ksLog.Error(this, "Error updating '" + path + "'.", e);
                ksPathUtils.Delete(tmpPath);
            }
        }

        /**
         * Caches all assets at the given path. Replaces missing references to the cached assets.
         * 
         * @param   string path of assets to cache.
         */
        private void CacheAssets(string path)
        {
            sfAssetInfo info = new sfAssetInfo();
            UObject asset = null;
            CacheAssets(path, -1, ref asset, ref info);
        }

        /**
         * Caches all assets at the given path. Replaces missing references to the cached assets. Returns the asset
         * info for the given asset.
         * 
         * @param   string path of assets to cache.
         * @param   UObject asset to get sub-asset path for.
         * @return  string sfAssetInfo asset info for the asset. Paths is empty if the asset was not found.
         */
        private sfAssetInfo CacheAssets(string path, UObject asset)
        {
            sfAssetInfo info = new sfAssetInfo();
            CacheAssets(path, -1, ref asset, ref info);
            return info;
        }

        /**
         * Caches all assets at the given path. Replaces missing references to the cached assets. Returns the asset at
         * the given index.
         * 
         * @param   string path of assets to cache.
         * @param   int index of sub-asset to get.
         * @return  UObject asset at the given index, or null if none was found.
         */
        private UObject CacheAssets(string path, int index)
        {
            UObject asset = null;
            sfAssetInfo info = new sfAssetInfo();
            CacheAssets(path, index, ref asset, ref info);
            return asset;
        }

        /**
         * Caches all assets at the given path. Replaces missing references to the cached assets. Optionally retrieves
         * a sub-asset by index or an asset index for an asset.
         * 
         * @param   string path of assets to cache.
         * @param   int index of sub-asset to retrieve. Pass negative number to not retrieve a sub asset.
         * @param   ref UObject asset - set to sub-asset at the given index if one is found. Otherwise
         *          retrieves the sub-asset path for this asset.
         * @param   ref sfAssetInfo assetInfo - set to info for asset if it is not null.
         */
        private void CacheAssets(
            string path,
            int index,
            ref UObject asset,
            ref sfAssetInfo assetInfo)
        {
            // Load all assets if this is not a scene asset (loading all assets from a scene asset causes an error)
            UObject[] assets = null;
            if (!path.EndsWith(".unity"))
            {
                assets = AssetDatabase.LoadAllAssetsAtPath(path);
            }
            if (assets == null || assets.Length == 0)
            {
                // Some assets (like folders) will return 0 results if you use LoadAllAssetsAtPath, but can be loaded
                // using LoadAssetAtPath.
                assets = new UObject[] { AssetDatabase.LoadAssetAtPath<UObject>(path) };
                if (assets[0] == null)
                {
                    return;
                }
            }
            else if (assets.Length > 1)
            {
                // Sub-asset order is not guaranteed so we sort based on type and name. This may fail if two sub-assets
                // have the exact same type and name...
                assets = new AssetSorter().Sort(assets, AssetDatabase.LoadAssetAtPath<UObject>(path));
            }
            for (int i = 0; i < assets.Length; i++)
            {
                UObject uobj = assets[i];
                if (uobj == null)
                {
                    continue;
                }
                sfAssetInfo info = new sfAssetInfo(uobj.GetType(), path, i);

                // If the cache contains a different asset for this path, this is an asset that was previously missing.
                UObject current;
                if (m_cache.TryGetValue(info, out current))
                {
                    if (current == uobj)
                    {
                        continue;
                    }
                    if (current != null)
                    {
                        // Map the stand-in to the correct asset so if we find references to the stand-in later, we can
                        // update them to the correct asset.
                        m_replacements[current] = uobj;
                    }
                    if (OnFindMissingAsset != null)
                    {
                        OnFindMissingAsset(info, uobj);
                    }
                }

                m_infoCache[uobj] = info;
                m_cache[info] = uobj;
                
                if (index == i)
                {
                    asset = uobj;
                }
                else if (asset == uobj)
                {
                    assetInfo = info;
                }

                if (OnCacheAsset != null)
                {
                    OnCacheAsset(info, uobj);
                }
            }
        }

        /**
         * Loads built-in assets into the cache. Built-in assets cannot be loaded programmatically, so we assign
         * references to them to a scriptable object in the editor, and load the scriptable object to get asset
         * references.
         */
        private void LoadBuiltInAssets()
        {
            CacheBuiltIns(ksIconUtility.Get().GetBuiltInIcons());

            sfBuiltInAssetsLoader loader = sfBuiltInAssetsLoader.Get();
            CacheBuiltIns(loader.LoadBuiltInAssets<Material>());
            CacheBuiltIns(loader.LoadBuiltInAssets<Texture2D>());
            CacheBuiltIns(loader.LoadBuiltInAssets<Sprite>());
            CacheBuiltIns(loader.LoadBuiltInAssets<LightmapParameters>());
            CacheBuiltIns(loader.LoadBuiltInAssets<Mesh>());
            CacheBuiltIns(loader.LoadBuiltInAssets<Font>());
            CacheBuiltIns(loader.LoadBuiltInAssets<Shader>());
        }

        /**
         * Loads stand-in template assets. When an asset is missing, we use a stand-in asset of the same type to
         * represent it.
         */
        private void LoadStandInTemplates()
        {
            CacheStandInFromBuiltIn<LightmapParameters>("Default-HighResolution");

            CacheStandInFromPath<Material>(sfPaths.StandIns + "Material.mat");
            CacheStandInFromPath<Texture2D>(sfPaths.Textures + "QuestionSmall.png");
            CacheStandInFromPath<Texture>(sfPaths.Textures + "QuestionSmall.png");
            CacheStandInFromPath<Sprite>(sfPaths.Textures + "QuestionSmall.png");
            CacheStandInFromPath<Mesh>(sfPaths.StandIns + "Cube.fbx");
            CacheStandInFromPath<AnimationClip>(sfPaths.StandIns + "Cube.fbx");
            CacheStandInFromPath(sfPaths.StandIns + "AudioMixer.mixer", 
                new ksReflectionObject(typeof(EditorWindow).Assembly, "UnityEditor.Audio.AudioMixerController").Type);
            CacheStandInFromPath(sfPaths.StandIns + "AudioMixer.mixer",
                new ksReflectionObject(typeof(EditorWindow).Assembly, 
                "UnityEditor.Audio.AudioMixerGroupController").Type);
            CacheStandInFromPath(sfPaths.StandIns + "AudioMixer.mixer",
                new ksReflectionObject(typeof(EditorWindow).Assembly, 
                "UnityEditor.Audio.AudioMixerSnapshotController").Type);
            CacheStandInFromPath<AudioClip>(sfPaths.StandIns + "AudioClip.wav");
            CacheStandInFromPath<Avatar>(sfPaths.StandIns + "StandIn.fbx");
            CacheStandInFromPath<UObject>(sfPaths.Textures + "QuestionSmall.png");
            CacheStandInFromPath<TextAsset>(sfPaths.StandIns + "TextAsset.txt");
            CacheStandInFromPath<MonoScript>(sfPaths.PackageRoot + "FusionRoot.cs");
            CacheStandInFromPath<Shader>(sfPaths.PackageRoot + "Shaders/Missing.shader");
            CacheStandInFromPath<ComputeShader>(sfPaths.StandIns + "DoNothing.compute");
            CacheStandInFromPath<RayTracingShader>(sfPaths.StandIns + "RayTracingShader.raytrace");
            CacheStandInFromPath<LightingDataAsset>(sfPaths.StandIns + "LightingData.asset");
            CacheStandInFromPath<Cubemap>(sfPaths.StandIns + "TextureCube.png");
            CacheStandInFromPath<Texture2DArray>(sfPaths.StandIns + "TextureArray.png");
            CacheStandInFromPath<Texture3D>(sfPaths.StandIns + "Texture3D.png");
            CacheStandInFromPath<StyleSheet>(sfPaths.StandIns + "StyleSheet.uss");
            CacheStandInFromPath<ThemeStyleSheet>(sfPaths.StandIns + "ThemeStyleSheet.tss");
            CacheStandInFromPath<VisualTreeAsset>(sfPaths.StandIns + "VisualTreeAsset.uxml");
            CacheStandInFromPath<SceneAsset>(sfPaths.StandIns + "Scene.unity");
            CacheStandInFromPath<DefaultAsset>(ksPathUtils.Clean(sfPaths.StandIns));

            // Cache stand-in library asset overrides. These assets are only used when creating a stand-in library
            // asset with a specific guid.
            
            // We cannot use a font template to create the stand-in instance because Unity logs an error when you try
            // to clone the font, so we instead use a generator for the instance.
            CacheStandInFromPath<Font>(sfPaths.StandIns + "DummyText.ttf", true);
            // We use a different material for the instance because we wanted a Magenta material that looks like
            // Unity's error material for stand-in instances and I couldn't figure out how to create that in Blender,
            // and we need a material from an .fbx or some other file extension that isn't specific to Unity in order
            // to create a library asset.
            CacheStandInFromPath<Material>(sfPaths.StandIns + "Cube.fbx", true);
        }

        /**
         * Attempt to cache a stand-in asset found at a known path location
         * 
         * @param   string assetPath
         * @param   bool isLibraryAssetOverride - if true, the asset will be added to the stand-in template library
         *          overrides cache which are only used to create a stand-in library asset with a specific guid when we
         *          want to use a different asset than what we use for the stand-in instance.
         */
        private void CacheStandInFromPath<T>(string assetPath, bool isLibraryAssetOverride = false) where T : UObject
        {
            CacheStandInFromPath(assetPath, typeof(T), isLibraryAssetOverride);
        }

        /**
         * Attempt to cache a stand-in asset found at a known path location
         * 
         * @param   string assetPath
         * @param   Type type of asset to cache.
         * @param   bool isLibraryAssetOverride - if true, the asset will be added to the stand-in template library
         *          overrides cache which are only used to create a stand-in library asset with a specific guid when we
         *          want to use a different asset than what we use for the stand-in instance.
         */
        private void CacheStandInFromPath(string assetPath, Type type, bool isLibraryAssetOverride = false)
        {
            if (type == null)
            {
                return;
            }
            UObject asset = AssetDatabase.LoadAssetAtPath(assetPath, type);
            if (asset != null)
            {
                if (isLibraryAssetOverride)
                {
                    m_standInTemplateLibraryOverrides[type] = asset;
                }
                else
                {
                    m_standInTemplates[type] = asset;
                }
                return;
            }
            ksLog.Warning("Unable to cache asset " + assetPath + " of type " + type);
        }

        /**
         * Attempt to cache a stand-in asset found in the built-in assets
         * 
         * @param   string assetName
         */
        private void CacheStandInFromBuiltIn<T>(string assetName) where T : UObject
        {
            sfBuiltInAssetsLoader loader = sfBuiltInAssetsLoader.Get();
            T[] assets = loader.LoadBuiltInAssets<T>();

            foreach (T asset in assets)
            {
                if (asset.name == assetName)
                {
                    m_standInTemplates[typeof(T)] = asset;
                    return;
                }
            }

            ksLog.Warning("Unable to cache built-in asset " + assetName + " of type " + typeof(T).ToString());
        }

        /**
         * Adds built-in assets to the cache.
         * 
         * @param   T[] assets to cache.
         */
        private void CacheBuiltIns<T>(T[] assets) where T : UObject
        {
            string className = typeof(T).ToString();
            foreach (T asset in assets)
            {
                if (asset != null)
                {
                    sfAssetInfo info = new sfAssetInfo(className, BUILT_IN_PREFIX + asset.name);
                    m_cache[info] = asset;
                    m_infoCache[asset] = info;
                }
            }
        }

        /**
         * Finds and caches all uncached built-ins of the given type using Resources.FindObjectOfTypeAll, which is
         * slow. There are some built-in assets that cannot be loaded the normal way, so we use this as a fallback when
         * we cannot load a built-in asset.
         * 
         * @param   Type type of asset to cache built-ins for.
         */
        private void FindUncachedBuiltInsSlow(Type type)
        {
            string className = type.ToString();
            foreach (UObject asset in Resources.FindObjectsOfTypeAll(type))
            {
                if (!m_infoCache.ContainsKey(asset) && IsBuiltInAsset(asset))
                {
                    sfAssetInfo info = new sfAssetInfo(className, BUILT_IN_PREFIX + asset.name);
                    m_cache[info] = asset;
                    m_infoCache[asset] = info;
                }
            }
        }

        /**
         * Called when assets are imported. Replaces missing asset references with the new assets.
         * 
         * @param   string[] paths to imported assets.
         */
        private void HandleImportAssets(string[] paths)
        {
            foreach (string path in paths)
            {
                CacheAssets(path);
            }
        }
    }
}
