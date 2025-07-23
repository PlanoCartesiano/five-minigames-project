using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;
using KS.SceneFusion2.Client;
using KS.SceneFusion;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /// <summary>
    /// Syncs navigation project settings through the <see cref="sfAssetTranslator"/> if a NavMeshSurface component is
    /// synced.
    /// </summary>
    [InitializeOnLoad]
    public class sfNavigation
    {
        private int m_lastAgentsCount;
        private UObject m_navigationSettings;
        private ksReflectionObject m_roNavigationWindow;
        private EditorWindow m_navigationWindow;

        /// <summary>Static constructor</summary>
        static sfNavigation()
        {
            sfObjectEventDispatcher.Get().OnInitialize += new sfNavigation().Initialize;
        }

        /// <summary>Constructor</summary>
        private sfNavigation()
        {

        }

        /// <summary>
        /// Initialization. Registers event handlers with the component and asset translators to handle syncing
        /// navigation settings.
        /// </summary>
        private void Initialize()
        {
            // NavMeshSurface is part of the Unity.AI.Navigation package which may not be part of the project, so we
            // try to load it with reflection.
            ksReflectionObject roNavMeshSurface = new ksReflectionObject("Unity.AI.Navigation",
                "Unity.AI.Navigation.NavMeshSurface", true);
            if (roNavMeshSurface.IsVoid)
            {
                return;
            }
            m_roNavigationWindow = new ksReflectionObject("Unity.AI.Navigation.Editor",
                    "Unity.AI.Navigation.Editor.NavigationWindow").GetField("s_NavigationWindow");
            sfComponentTranslator componentTranslator = sfObjectEventDispatcher.Get()
                .GetTranslator<sfComponentTranslator>(sfType.Component);
            // Start syncing navigation settings when we find a NavMeshSurface component.
            componentTranslator.ObjectInitializers.Add(roNavMeshSurface.Type, (sfObject obj, Component component) =>
            {
                Start(true);
            });
            componentTranslator.ComponentInitializers.Add(roNavMeshSurface.Type, (sfObject obj, Component component) =>
            {
                Start(false);
            });
            sfAssetTranslator assetTranslator = sfObjectEventDispatcher.Get()
                .GetTranslator<sfAssetTranslator>(sfType.Asset);
            assetTranslator.PostPropertyChange.Add<UObject>("m_Settings", (UObject uobj, sfBaseProperty prop) =>
            {
                if (uobj == m_navigationSettings)
                {
                    m_lastAgentsCount = NavMesh.GetSettingsCount();
                }
            });
            assetTranslator.PostUObjectChange.Add<UObject>((UObject uobj) =>
            {
                if (uobj == m_navigationSettings)
                {
                    RefreshWindow();
                }
            });
        }

        /// <summary>
        /// Starts polling for new agents added to the navigation settings, and optionally uploads navigation settings
        /// if they are already uploaded. We have to poll for new agents because Unity has no events when agents are
        /// added and doesn't register an undo transaction.
        /// </summary>
        /// <param name="uploadSettings">
        /// If true, creates an sfObject for the navigation settings through the <see cref="sfAssetTranslator"/>
        /// </param>
        private void Start(bool uploadSettings)
        {
            if (m_navigationSettings == null)
            {
                m_navigationSettings = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/NavMeshAreas.asset");
                m_lastAgentsCount = NavMesh.GetSettingsCount();
                SceneFusion.Get().PreUpdate += PollNewAgents;
                SceneFusion.Get().Service.OnDisconnect += Stop;
                if (uploadSettings)
                {
                    sfAssetTranslator assetTranslator = sfObjectEventDispatcher.Get()
                        .GetTranslator<sfAssetTranslator>(sfType.Asset);
                    assetTranslator.Create(m_navigationSettings);
                }
            }
        }

        /// <summary>Stops polling for new agents added to the navigation settings.</summary>
        /// <param name="session">Session we disconnected from. Unused.</param>
        /// <param name="errorMessage">Disconnect error message. Unused.</param>
        private void Stop(sfSession session, string errorMessage)
        {
            m_navigationSettings = null;
            SceneFusion.Get().PreUpdate -= PollNewAgents;
        }

        /// <summary>Syncs changes if new agents were added to the navigation settings.</summary>
        /// <param name="deltaTime">Deltatime in seconds since the last update. Unused.</param>
        private void PollNewAgents(float deltaTime)
        {
            int count = NavMesh.GetSettingsCount();
            if (count > m_lastAgentsCount)
            {
                sfObject obj = sfObjectMap.Get().GetSFObject(m_navigationSettings);
                if (obj != null)
                {
                    sfDictionaryProperty properties = (sfDictionaryProperty)obj.Property;
                    if (obj.IsLocked)
                    {
                        sfPropertyManager.Get().ApplyProperties(m_navigationSettings, properties);
                    }
                    else
                    {
                        sfPropertyManager.Get().SendPropertyChanges(m_navigationSettings, properties);
                    }
                }
            }
            m_lastAgentsCount = count;
        }

        /// <summary>Refreshes the navigation window, if it is open.</summary>
        private void RefreshWindow()
        {
            if (m_navigationWindow == null)
            {
                m_navigationWindow = m_roNavigationWindow.GetValue() as EditorWindow;
                if (m_navigationWindow == null)
                {
                    return;
                }
            }
            m_navigationWindow.Repaint();
        }
    }
}
