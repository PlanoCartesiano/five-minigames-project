#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using KS.Reactor;
using KS.SceneFusion;

namespace KS.SceneFusion2.Unity
{
    /**
     * Attached to synced game objects with missing prefabs.
     */
    [AddComponentMenu("")]
    public class sfMissingPrefab : sfBaseComponent
    {
        /**
         * Prefab path
         */
        public string PrefabPath
        {
            get { return m_prefabPath; }
            set { m_prefabPath = value; }
        }
        [SerializeField]
        private string m_prefabPath;

        /**
         * The child index of this object in the missing prefab. -1 if this is the root of the missing prefab.
         */
        public int ChildIndex
        {
            get { return m_childIndex; }
            set { m_childIndex = value; }
        }
        [SerializeField]
        private int m_childIndex = -1;

        /**
         * Logs a warning for the missing prefab.
         */
        private void Awake()
        {
            if (m_childIndex < 0)
            {
                ksLog.Warning(this, gameObject.name + " has missing prefab '" + m_prefabPath + "'.", gameObject);
            }
            else if (transform.parent == null || transform.parent.GetComponent<sfMissingPrefab>() == null)
            {
                ksLog.Warning(this, gameObject.name + " has missing prefab child '" + m_prefabPath + "'.", gameObject);
            }
        }
    }
}
#endif