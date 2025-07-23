using UnityEditor;
using UnityEngine;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Custom editor for sfMissingPrefab.
     */
    [CustomEditor(typeof(sfMissingPrefab))]
    internal class sfMissingPrefabEditor : UnityEditor.Editor
    {
        /**
         * Creates the GUI. Displays the path to the missing prefab in a warning box.
         */
        public override void OnInspectorGUI()
        {
            sfMissingPrefab script = target as sfMissingPrefab;
            if (script != null)
            {
                if (SceneFusion.Get().Service.IsConnected)
                {
                    // Prevent removing the script while in a session.
                    script.hideFlags |= HideFlags.NotEditable;
                }
                else
                {
                    script.hideFlags &= ~HideFlags.NotEditable;
                }
                string message = "Missing prefab: " + script.PrefabPath;
                if (script.ChildIndex >= 0)
                {
                    message += "\nChild index: " + script.ChildIndex;
                }
                // End disabled group so the warning box does not appear faded when the component is not editable.
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.HelpBox(message, MessageType.Warning);
                EditorGUI.BeginDisabledGroup((script.hideFlags & HideFlags.NotEditable) == HideFlags.NotEditable);
            }
        }   
    }
}
