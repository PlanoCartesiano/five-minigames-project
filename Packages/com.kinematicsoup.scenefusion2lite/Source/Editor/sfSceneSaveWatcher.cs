using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Provides pre and post scene save events.
     */
    public class sfSceneSaveWatcher : UnityEditor.AssetModificationProcessor
    {
        /**
         * Pre save event handler.
         * 
         * @param   Scene scene that is being saved.
         */
        public delegate void PreSaveHandler(Scene scene);

        /**
         * Post save event handler.
         */
        public delegate void PostSaveHandler();

        /**
         * Invoked before a scene is saved.
         */
        public event PreSaveHandler PreSave;

        /**
         * Invoked after scenes are saved.
         */
        public event PostSaveHandler PostSave;

        /**
         * @return  sfSceneWatcher singleton instance
         */
        public static sfSceneSaveWatcher Get()
        {
            return m_instance;
        }
        private static sfSceneSaveWatcher m_instance = new sfSceneSaveWatcher();

        /**
         * Singleton constructor
         */
        private sfSceneSaveWatcher()
        {

        }

        /**
         * Called before assets are saved. Dispatches events for scenes that are about to be saved, then if any scenes
         * were saved, dispatches a PostSave event on the next pre update.
         * 
         * @param   string[] paths to assets that will be saved.
         * @return  string[] paths to assets that will be saved.
         */
        public static string[] OnWillSaveAssets(string[] paths)
        {
            if (m_instance.PreSave != null || m_instance.PostSave != null)
            {
                bool savingScene = false;
                foreach (string path in paths)
                {
                    if (path.EndsWith(".unity"))
                    {
                        Scene scene = SceneManager.GetSceneByPath(path);
                        if (scene.IsValid() && scene.isLoaded)
                        {
                            savingScene = true;
                            if (m_instance.PreSave != null)
                            {
                                m_instance.PreSave(scene);
                            }
                        }
                    }
                }
                if (savingScene && m_instance.PostSave != null)
                {
                    SceneFusion.Get().PreUpdate += m_instance.InvokePostSave;
                }
            }
            return paths;
        }

        /**
         * Invokes the PostSave event.
         * 
         * @param   float deltaTime in seconds since the last update.
         */
        private void InvokePostSave(float deltaTime)
        {
            SceneFusion.Get().PreUpdate -= InvokePostSave;
            if (PostSave != null)
            {
                PostSave();
            }
        }
    }
}
