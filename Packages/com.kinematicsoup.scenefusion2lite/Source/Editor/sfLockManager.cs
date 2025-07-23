using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using KS.Unity.Editor;
using KS.Reactor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * This class manages the lock object for all synced game objects.
     */
    class sfLockManager
    {
        // Name of hidden lock object.
        public const string LOCK_OBJECT_NAME = "__ksLockObject";

        // Lock materials
        private Dictionary<sfUser, Material> m_userToMaterial = new Dictionary<sfUser, Material>();
        private Dictionary<sfUser, Material> m_userToIconMaterial = new Dictionary<sfUser, Material>();

        private HashSet<GameObject> m_gameObjectsWithStaleLockShader = new HashSet<GameObject>();
        private HashSet<GameObject> m_gameObjectsWithStaleLODLock = new HashSet<GameObject>();

        // Quad mesh used for the icon lock object
        private Mesh m_quadMesh = null;

        /**
         * @return  sfLockManager singleton instance.
         */
        public static sfLockManager Get()
        {
            return m_instance;
        }
        private static sfLockManager m_instance = new sfLockManager();

        /**
         * Constructor
         */
        private sfLockManager()
        {
            Mesh[] builtInMeshes = sfBuiltInAssetsLoader.Get().LoadBuiltInAssets<Mesh>();
            foreach (Mesh mesh in builtInMeshes)
            {
                if (mesh.name == "Quad")
                {
                    m_quadMesh = mesh;
                    break;
                }
            }
        }

        /**
         * Starts monitoring undo operations.
         */
        public void Start()
        {
            SceneFusion.Get().OnUpdate += Update;
            sfConfig.Get().UI.OnToggleLockShaders += ToggleLockShaders;

            sfSession session = SceneFusion.Get().Service.Session;
            session.OnUserJoin += OnUserColorChange;
            session.OnUserLeave += OnUserLeave;
            session.OnUserColorChange += OnUserColorChange;
        }

        /**
         * Stops monitoring undo operations.
         */
        public void Stop()
        {
            SceneFusion.Get().OnUpdate -= Update;
            sfConfig.Get().UI.OnToggleLockShaders -= ToggleLockShaders;

            sfSession session = SceneFusion.Get().Service.Session;
            session.OnUserJoin -= OnUserColorChange;
            session.OnUserLeave -= OnUserLeave;
            session.OnUserColorChange -= OnUserColorChange;

            // Destroy materials
            foreach (Material material in m_userToMaterial.Values)
            {
                UObject.DestroyImmediate(material);
            }
            m_userToMaterial.Clear();
            foreach (Material material in m_userToIconMaterial.Values)
            {
                UObject.DestroyImmediate(material);
            }
            m_userToIconMaterial.Clear();
        }

        /**
         * Called every frame.
         * 
         * @param   float deltaTime since the last frame.
         */
        private void Update(float deltaTime)
        {
            if (!sfConfig.Get().UI.ShowLockShaders)
            {
                return;
            }

            // Update lock LOD
            foreach (GameObject gameObject in m_gameObjectsWithStaleLODLock)
            {
                UpdateLockLOD(gameObject, FindLockObject(gameObject));
            }
            m_gameObjectsWithStaleLODLock.Clear();

            // Refresh stale lock objects
            foreach (GameObject gameObject in m_gameObjectsWithStaleLockShader)
            {
                RefreshLock(gameObject);
            }
            m_gameObjectsWithStaleLockShader.Clear();

            // If the icons in the scene have potential changes, refresh all lock objects.
            if (sfAnnotationUtils.Get().UpdateAnnotationCache())
            {
                foreach (GameObject gameObject in sfUnityUtils.IterateGameObjects())
                {
                    RefreshLock(gameObject);
                }
            }
        }

        /**
         * Checks if a game object is a lock object. To be a lock object, the game object must have the
         * LOCK_OBJECT_NAME and have hideflags HideAndDontSave.
         * 
         * @param   GameObject gameObject to check.
         * @return  bool true if the game object is a lock object.
         */
        public bool IsLockObject(GameObject gameObject)
        {
            return gameObject != null && gameObject.name == LOCK_OBJECT_NAME &&
                (gameObject.hideFlags & HideFlags.HideAndDontSave) != HideFlags.None;
        }

        /**
         * Finds the lock object for the given game object. Returns null if not found.
         * 
         * @param   GameObject gameObject to find lock object for
         * @param   bool findWhenLocksDisabled - if false, will not look for a lock object if lock shaders are disabled
         *          in the config.
         * @return  GameObject
         */
        public GameObject FindLockObject(GameObject gameObject, bool findWhenLocksDisabled = false)
        {
            if ((sfConfig.Get().UI.ShowLockShaders || findWhenLocksDisabled) && gameObject != null)
            {
                foreach (Transform childTransform in gameObject.transform)
                {
                    if (IsLockObject(childTransform.gameObject))
                    {
                        return childTransform.gameObject;
                    }
                }
            }
            return null;
        }

        /**
         * Creates the lock object for the given game object
         * 
         * @param   GameObject gameObject to create lock object for
         * @param   sfObject obj - sfObject of the game object
         */
        public void CreateLockObject(GameObject gameObject, sfObject obj)
        {
            if (!sfConfig.Get().UI.ShowLockShaders || gameObject == null || obj == null)
            {
                return;
            }

            Material lockMaterial;
            Material lockIconMaterial;
            GetLockMaterials(obj.LockOwner, out lockMaterial, out lockIconMaterial);
            GameObject lockObject = FindLockObject(gameObject);
            if (lockObject != null)
            {
                sfMaterialUtils.Get().UpdateMaterialOnObject(
                    lockObject,
                    lockMaterial,
                    lockMaterial,
                    lockIconMaterial);
                return;
            }

            MeshRenderer meshRenderer = gameObject.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                MeshFilter meshFilter = gameObject.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    lockObject = new GameObject();
                    MeshFilter lockMesh = lockObject.AddComponent<MeshFilter>();
                    lockMesh.sharedMesh = meshFilter.sharedMesh;

                    MeshRenderer lockRenderer = lockObject.AddComponent<MeshRenderer>();
                    sfMaterialUtils.Get().UpdateMaterialsOnRenderer(
                        lockRenderer,
                        lockMaterial,
                        meshRenderer.sharedMaterials.Length);
                }
                else
                {
                    TextMesh text = gameObject.GetComponent<TextMesh>();
                    if (text != null)
                    {
                        lockObject = new GameObject();
                        MeshRenderer lockRenderer = lockObject.AddComponent<MeshRenderer>();
                        sfMaterialUtils.Get().UpdateMaterialsOnRenderer(
                            lockRenderer,
                            lockMaterial,
                            meshRenderer.sharedMaterials.Length);

                        TextMesh lockText = lockObject.AddComponent<TextMesh>();
                        lockText.text = text.text;
                        lockText.characterSize = text.characterSize;
                        lockText.fontSize = text.fontSize;
                        lockText.tabSize = text.tabSize;
                        lockText.font = text.font;
                        lockText.fontStyle = text.fontStyle;
                        lockText.offsetZ = text.offsetZ;
                        lockText.alignment = text.alignment;
                        lockText.anchor = text.anchor;
                        lockText.lineSpacing = text.lineSpacing;
                        lockText.richText = text.richText;
                    }
                }
            }
            else
            {
                SpriteRenderer spriteRenderer = gameObject.GetComponent<SpriteRenderer>();
                if (spriteRenderer != null)
                {
                    lockObject = new GameObject();
                    SpriteRenderer lockSprite = lockObject.AddComponent<SpriteRenderer>();
                    lockSprite.material = lockMaterial;
                    lockSprite.sprite = spriteRenderer.sprite;
                    lockSprite.sortingLayerID = spriteRenderer.sortingLayerID;
                    lockSprite.sortingOrder = spriteRenderer.sortingOrder;
                    lockSprite.drawMode = spriteRenderer.drawMode;
                    lockSprite.size = spriteRenderer.size;
                    lockSprite.tileMode = spriteRenderer.tileMode;
                    lockSprite.adaptiveModeThreshold = spriteRenderer.adaptiveModeThreshold;
                    lockSprite.maskInteraction = spriteRenderer.maskInteraction;
                    lockSprite.flipX = spriteRenderer.flipX;
                    lockSprite.flipY = spriteRenderer.flipY;
                }
            }

            SkinnedMeshRenderer skinnedMesh = gameObject.GetComponent<SkinnedMeshRenderer>();
            if (skinnedMesh != null)
            {
                if (lockObject == null)
                {
                    lockObject = new GameObject();
                }
                SkinnedMeshRenderer lockSkin = lockObject.AddComponent<SkinnedMeshRenderer>();
                lockSkin.bones = skinnedMesh.bones;
                lockSkin.sharedMesh = skinnedMesh.sharedMesh;
                lockSkin.rootBone = skinnedMesh.rootBone;
                sfMaterialUtils.Get().UpdateMaterialsOnRenderer(
                    lockSkin,
                    lockMaterial,
                    skinnedMesh.sharedMaterials.Length);
            }

            LineRenderer lineRenderer = gameObject.GetComponent<LineRenderer>();
            if (lineRenderer != null)
            {
                if (lockObject == null)
                {
                    lockObject = new GameObject();
                }
                LineRenderer lockLine = lockObject.AddComponent<LineRenderer>();
                Vector3[] positions = new Vector3[lineRenderer.positionCount];
                lockLine.positionCount = lineRenderer.positionCount;
                lineRenderer.GetPositions(positions);
                lockLine.SetPositions(positions);
                lockLine.startWidth = lineRenderer.startWidth;
                lockLine.endWidth = lineRenderer.endWidth;
                lockLine.useWorldSpace = lineRenderer.useWorldSpace;
                lockLine.material = lockMaterial;
            }

            if (lockObject == null)
            {
                sfDictionaryProperty properties = obj.Property as sfDictionaryProperty;
                if (properties.HasField("m_Icon") || sfAnnotationUtils.Get().HasComponentIcon(gameObject))
                {
                    lockObject = new GameObject();
                    MeshFilter lockMesh = lockObject.AddComponent<MeshFilter>();
                    lockMesh.sharedMesh = m_quadMesh;

                    MeshRenderer lockRenderer = lockObject.AddComponent<MeshRenderer>();
                    lockRenderer.sharedMaterial = lockIconMaterial;
                }
            }

            LODGroup lodGroup = gameObject.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                if (lockObject == null)
                {
                    lockObject = new GameObject();
                }
                MarkLockLODStale(gameObject);
            }

            if (lockObject != null)
            {
                lockObject.name = LOCK_OBJECT_NAME;
                sfComponentUtils.SetParent(lockObject, gameObject);
                // Set lock object as the first child so it won't be rendered on top of the other children.
                lockObject.transform.SetAsFirstSibling();
                lockObject.transform.localPosition = Vector3.zero;
                lockObject.transform.localRotation = Quaternion.identity;
                lockObject.transform.localScale = Vector3.one;
                // The hide flag need to be set after we have set the scale.
                // Otherwise unity may log out an error if the scale value is NAN.
                lockObject.hideFlags = HideFlags.HideAndDontSave;
                sfUI.Get().MarkSceneViewStale();
            }
        }

        /**
         * Marks the given game object's lock LODs stale.
         * 
         * @param   GameObject gameObject
         */
        public void MarkLockLODStale(GameObject gameObject)
        {
            if (sfConfig.Get().UI.ShowLockShaders)
            {
                m_gameObjectsWithStaleLODLock.Add(gameObject);
            }
        }

        /**
         * Updates the lock object's LODs with the game object.
         * 
         * @param   GameObject gameObject to copy LODs settings from
         * @param   GameObject lockObject to update LODs for
         */
        private void UpdateLockLOD(GameObject gameObject, GameObject lockObject)
        {
            if (gameObject == null || lockObject == null)
            {
                return;
            }
            LODGroup lodGroup = gameObject.GetComponent<LODGroup>();
            if (lodGroup == null)
            {
                return;
            }
            LODGroup lockLOD = lockObject.GetComponent<LODGroup>();
            if (lockLOD == null)
            {
                lockLOD = lockObject.AddComponent<LODGroup>();
            }
            lockLOD.animateCrossFading = lodGroup.animateCrossFading;
            lockLOD.fadeMode = lodGroup.fadeMode;
            lockLOD.enabled = lodGroup.enabled;
            LOD[] lods = lodGroup.GetLODs();
            for (int i = 0; i < lods.Length; i++)
            {
                Renderer[] renderers = lods[i].renderers;
                for (int j = 0; j < renderers.Length; j++)
                {
                    Renderer renderer = renderers[j];
                    if (renderer == null)
                    {
                        continue;
                    }
                    GameObject rendererGameObjectLockObject = FindLockObject(renderer.gameObject);
                    if (rendererGameObjectLockObject != null)
                    {
                        renderers[j] = rendererGameObjectLockObject.GetComponent(renderer.GetType()) as Renderer;
                    }
                    else
                    {
                        renderers[j] = null;
                    }
                }
            }
            lockLOD.SetLODs(lods);
            lockLOD.size = lodGroup.size;
        }

        /**
         * Updates the material on the lock object of the given game object.
         * 
         * @param   GameObject gameObject
         * @param   sfObject obj
         */
        public void UpdateLockMaterial(GameObject gameObject, sfObject obj)
        {
            Material lockMaterial = null;
            Material lockIconMaterial = null;
            sfLockManager.Get().GetLockMaterials(obj.LockOwner, out lockMaterial, out lockIconMaterial);
            GameObject lockObject = sfLockManager.Get().FindLockObject(gameObject);
            if (lockObject != null)
            {
                sfMaterialUtils.Get().UpdateMaterialOnObject(
                    lockObject,
                    lockMaterial,
                    lockMaterial,
                    lockIconMaterial);
                sfUI.Get().MarkSceneViewStale();
                return;
            }
        }

        /**
         * Gets lock materials for the given user. Creates the materials if they don't already exist.
         * 
         * @param   sfUser user to create lock materials for
         * @param   out Material lockMaterial
         * @param   out Material lockIconMaterial
         * @return  bool - true if the given user is not null
         */
        private bool GetLockMaterials(sfUser user, out Material lockMaterial, out Material lockIconMaterial)
        {
            if (user == null)
            {
                lockMaterial = sfUserMaterials.LockMaterial;
                lockIconMaterial = sfUserMaterials.LockIconMaterial;
                return false;
            }
            if (!m_userToMaterial.TryGetValue(user, out lockMaterial) ||
                !m_userToIconMaterial.TryGetValue(user, out lockIconMaterial))
            {
                sfUserMaterials.CreateLockMaterialsForPlayer(user.Color, out lockMaterial, out lockIconMaterial);
                m_userToMaterial[user] = lockMaterial;
                m_userToIconMaterial[user] = lockIconMaterial;
            }
            return true;
        }

        /**
         * Recreates the lock object for the given game object
         * 
         * @param   GameObject gameObject to refresh lock for
         */
        public void RefreshLock(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);

            if (obj == null || !obj.IsLocked)
            {
                return;
            }

            GameObject lockObject = FindLockObject(gameObject);
            if (lockObject != null)
            {
                GameObject.DestroyImmediate(lockObject);
            }
            CreateLockObject(gameObject, obj);
        }

        /**
         * Marks the given game object's lock object stale.
         * 
         * @param   GameObject gameObject
         */
        public void MarkLockObjectStale(GameObject gameObject)
        {
            sfUI.Get().MarkSceneViewStale();
            if (sfConfig.Get().UI.ShowLockShaders)
            {
                m_gameObjectsWithStaleLockShader.Add(gameObject);
            }
        }

        /**
         * Called when a user leaves the session.
         * Destroys the user's lock material and remove it from the material dictionary.
         *
         * @param   sfUser user
         */
        private void OnUserLeave(sfUser user)
        {
            Material material = null;
            if (m_userToMaterial.TryGetValue(user, out material))
            {
                UObject.DestroyImmediate(material);
                m_userToMaterial.Remove(user);
            }
            if (m_userToIconMaterial.TryGetValue(user, out material))
            {
                UObject.DestroyImmediate(material);
                m_userToIconMaterial.Remove(user);
            }
        }

        /**
         * Called when a user's color changes. Updates the lock materials' color.
         *
         * @param   sfUser user
         */
        private void OnUserColorChange(sfUser user)
        {
            Material lockMaterial = null;
            Material lockIconMaterial = null;
            if (GetLockMaterials(user, out lockMaterial, out lockIconMaterial))
            {
                lockMaterial.SetColor("m_colour", user.Color);
                lockIconMaterial.SetColor("m_colour", user.Color);
            }
        }

        /**
         * Toggles lock shader visibility.
         * 
         * @param   bool toggle - are lock shaders visible?
         */
        private void ToggleLockShaders(bool toggle)
        {
            foreach (GameObject gameObject in sfUnityUtils.IterateGameObjects())
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(gameObject);
                if (obj != null && obj.IsLocked)
                {
                    if (toggle)
                    {
                        CreateLockObject(gameObject, obj);
                    }
                    else
                    {
                        GameObject lockObject = FindLockObject(gameObject, true);
                        if (lockObject != null)
                        {
                            UObject.DestroyImmediate(lockObject);
                            sfUI.Get().MarkSceneViewStale();
                        }
                    }
                }
            }
        }
    }
}
