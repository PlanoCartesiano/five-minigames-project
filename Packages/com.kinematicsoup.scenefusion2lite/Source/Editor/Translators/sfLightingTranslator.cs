using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Manages syncing of lighting properties.
     */
    public class sfLightingTranslator : sfBaseUObjectTranslator
    {
        private EditorWindow m_lightingWindow;

        /**
         * Initialization
         */
        public override void Initialize()
        {
            sfSceneTranslator sceneTranslator = sfObjectEventDispatcher.Get().GetTranslator<sfSceneTranslator>(
                sfType.Scene);
            sceneTranslator.PreUploadScene += CreateLightingObjects;

            // Do not sync the 'Auto Generate' lighting setting.
            sfPropertyManager.Get().Blacklist.Add<LightingSettings>("m_GIWorkflowMode");

            PostUObjectChange.Add<UObject>((UObject uobj) => RefreshWindow());
            sfAssetTranslator translator = sfObjectEventDispatcher.Get().GetTranslator<sfAssetTranslator>(sfType.Asset);
            translator.PostUObjectChange.Add<LightingSettings>((UObject uobj) => RefreshWindow());
        }

        /**
         * Creates sfObjects for a scene's LightingSettings and RenderSettings as child objects of the scene object.
         * 
         * @param   Scene scene to create lighting objects for.
         * @param   sfObject sceneObject to make the lighting objects children of.
         */
        private void CreateLightingObjects(Scene scene, sfObject sceneObj)
        {
            // We can only get the lighting objects for this scene when it is the active scene.
            sfUnityUtils.WithActiveScene(scene, () =>
            {
                sceneObj.AddChild(CreateObject(GetLightmapSettings(), sfType.LightmapSettings));
                sceneObj.AddChild(CreateObject(GetRenderSettings(), sfType.RenderSettings));
            });
        }

        /**
         * Called when a lighting object is created by another user.
         * 
         * @param   sfObject obj that was created.
         * @param   int childIndex of the new object. -1 if the object is a root.
         */
        public override void OnCreate(sfObject obj, int childIndex)
        {
            if (obj.Parent == null)
            {
                ksLog.Warning(this, obj.Type + " object has no parent.");
                return;
            }
            sfSceneTranslator translator = sfObjectEventDispatcher.Get()
                .GetTranslator<sfSceneTranslator>(sfType.Scene);
            Scene scene = translator.GetScene(obj.Parent);
            sfUnityUtils.WithActiveScene(scene, () =>
            {
                UObject uobj = null;
                switch (obj.Type)
                {
                    case sfType.LightmapSettings: uobj = GetLightmapSettings(); break;
                    case sfType.RenderSettings: uobj = GetRenderSettings(); break;
                }
                if (uobj != null)
                {
                    sfObjectMap.Get().Add(obj, uobj);
                    sfPropertyManager.Get().ApplyProperties(uobj, (sfDictionaryProperty)obj.Property);
                }
            });
        }

        /**
         * Called when a locally-deleted object is confirmed as deleted. Removes the object from the sfObjectMap.
         * 
         * @param   sfObject obj that was confirmed as deleted.
         * @param   bool unsubscribed - true if the deletion occurred because we unsubscribed from the object's parent.
         */
        public override void OnConfirmDelete(sfObject obj, bool unsubscribed)
        {
            sfObjectMap.Get().Remove(obj);
        }

        /**
         * Gets the lightmap settings object for the active scene.
         */
        private LightmapSettings GetLightmapSettings()
        {
            return new ksReflectionObject(typeof(LightmapEditorSettings))
                .GetMethod("GetLightmapSettings").Invoke() as LightmapSettings;
        }

        /**
         * Gets the render settings object for the active scene.
         */
        private RenderSettings GetRenderSettings()
        {
            return new ksReflectionObject(typeof(RenderSettings))
                .GetMethod("GetRenderSettings").Invoke() as RenderSettings;
        }

        /**
         * Refreshes the lighting window.
         */
        private void RefreshWindow()
        {
            if (m_lightingWindow == null)
            {
                m_lightingWindow = ksEditorUtils.FindWindow("LightingWindow");
                if (m_lightingWindow == null)
                {
                    return;
                }
            }
            m_lightingWindow.Repaint();
        }
    }
}
