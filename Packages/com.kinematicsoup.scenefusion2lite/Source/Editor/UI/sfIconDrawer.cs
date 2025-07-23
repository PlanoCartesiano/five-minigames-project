using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using KS.SceneFusion;
using KS.SceneFusion2.Client;
using KS.Reactor;
using KS.Unity.Editor;
using UObject = UnityEngine.Object;

namespace KS.SceneFusion2.Unity.Editor
{
    /**
     * Draws icons in the hierarchy and project browser indicating the sync/lock/notification status of objects.
     */
    public class sfIconDrawer
    {
        /**
         * @return  sfIconDrawer singleton instance
         */
        public static sfIconDrawer Get()
        {
            return m_instance;
        }
        private static sfIconDrawer m_instance = new sfIconDrawer();

        // The icons are actually 16x16, but for some reason Unity shrinks them by 1.
        private const float ICON_SIZE = 17f;

        /**
         * Singleton constructor
         */
        private sfIconDrawer()
        {
        }

        /**
         * Starts drawing icons.
         */
        public void Start()
        {
            EditorApplication.hierarchyWindowItemOnGUI += DrawHierarchyItem;
            // ProjectWindowItemInstanceOnGUI is preferrable to projectWindowItemOnGUI because it distinguishes between
            // assets and subassets. It does not exist prior to to 2022.1, so, in older versions on Unity, all
            // subassets will have the same icon as the main asset since we cannot tell if we are drawing the main
            // asset or a subasset.
#if UNITY_2022_1_OR_NEWER
            EditorApplication.projectWindowItemInstanceOnGUI += DrawProjectItem;
#else
            EditorApplication.projectWindowItemOnGUI += DrawProjectItem;
#endif
            sfConfig.Get().UI.OnHierarchyIconOffsetChange += HandleHierarchyOffsetChange;
            sfConfig.Get().UI.OnProjectBrowserIconOffsetChange += HandleProjectBrowserOffsetChange;
        }

        /**
         * Stops drawing icons.
         */
        public void Stop()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= DrawHierarchyItem;
#if UNITY_2022_1_OR_NEWER
            EditorApplication.projectWindowItemInstanceOnGUI -= DrawProjectItem;
#else
            EditorApplication.projectWindowItemOnGUI -= DrawProjectItem;
#endif
            sfConfig.Get().UI.OnHierarchyIconOffsetChange -= HandleHierarchyOffsetChange;
            sfConfig.Get().UI.OnProjectBrowserIconOffsetChange -= HandleProjectBrowserOffsetChange;
            sfUI.Get().MarkProjectBrowserStale();
        }

        /**
         * Draws icons for a game object in the hierarchy window.
         *
         * @param   int instanceId of game object to draw icons for.
         * @param Rect area the game object label was drawn in.
         */
        private void DrawHierarchyItem(int instanceId, Rect area)
        {
            UObject uobj = EditorUtility.InstanceIDToObject(instanceId);
            if (uobj == null)
            {
                return;
            }
            area.x += area.width + 1f - ICON_SIZE - sfConfig.Get().UI.HierarchyIconOffset;
            area.y -= 1f;
            area.width = ICON_SIZE;
            DrawIcons(uobj, area);
        }

#if UNITY_2022_1_OR_NEWER
        /**
         * Draws icons for an asset in the project browser.
         * 
         * @param   int instanceId of asset to draw icons for.
         * @param   Rect area the asset was drawn in.
         */
        private void DrawProjectItem(int instanceId, Rect area)
        {
            UObject asset = EditorUtility.InstanceIDToObject(instanceId);
            DrawProjectItem(asset, area);
        }
#else
        /**
         * Draws icons for an asset in the project browser.
         * 
         * @param   string guid of the asset to draw icons for.
         * @param   Rect area the asset was drawn in.
         */
        private void DrawProjectItem(string guid, Rect area)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            UObject asset = AssetDatabase.LoadMainAssetAtPath(path);
            DrawProjectItem(asset, area);
        }
#endif
        /**
         * Draws icons for an asset in the project browser.
         * 
         * @param   UObject asset to draw icons for.
         * @param   Rect area the asset was drawn in.
         */
        private void DrawProjectItem(UObject asset, Rect area)
        {
            if (asset == null)
            {
                return;
            }
            area.x += Math.Clamp(area.width - ICON_SIZE - 1f - sfConfig.Get().UI.ProjectBrowserIconOffset.x,
                0, Mathf.Max(0, area.width - ICON_SIZE + 2f));
            area.y += Math.Clamp(area.height - 32f - sfConfig.Get().UI.ProjectBrowserIconOffset.y,
                0, Mathf.Max(0f, area.height - ICON_SIZE));
            area.width = ICON_SIZE;
            area.height = ICON_SIZE;
            DrawIcons(asset, area);
        }

        /**
         * Draws icons for an object.
         * 
         * @param   UObject uobj to draw icons for.
         * @param   Rect area to draw in.
         */
        private void DrawIcons(UObject uobj, Rect area)
        {
            sfObject obj = sfObjectMap.Get().GetSFObject(uobj);
            if (obj != null && obj.IsCreated)
            {
                DrawSyncedIcon(uobj, obj, area);
                area.x -= area.width - 1f;
            }

            ksLinkedList<sfNotification> notifications = sfNotificationManager.Get().GetNotifications(uobj, true);
            if (notifications != null && notifications.Count > 0)
            {
                // The notification icon has an empty border around it. We need to increase the size by 1 to make it
                // display the same size as our other icons.
                area.width += 1f;
                area.height += 1f;
                DrawNotificationIcon(notifications, area);
            }
        }

        /**
         * Draws either a green checkmark icon or a lock icon for an object based on its lock state.
         * 
         * @param   UObject uobj to draw icon for.
         * @param   sfObject obj for the uobject.
         * @param   Rect area to draw icon in.
         */
        private void DrawSyncedIcon(UObject uobj, sfObject obj, Rect area)
        {
            Texture2D icon;
            string tooltip;
            if (obj.CanEdit)
            {
                icon = sfTextures.Check;
                tooltip = "Synced and unlocked";
            }
            else if (obj.IsFullyLocked)
            {
                icon = sfTextures.Lock;
                tooltip = "Fully locked by " + obj.LockOwner.Name + ". Property and child editing disabled.";
                GUI.color = obj.LockOwner.Color;
            }
            else
            {
                icon = sfTextures.Lock;
                tooltip = "Partially Locked. Property editing disabled.";
                if (Event.current.type == EventType.ContextClick)
                {
                    GameObject gameObject = uobj as GameObject;
                    if (gameObject != null)
                    {
                        // Temporarly make the object editable so we can add children to it via the context menu.
                        sfGameObjectTranslator translator = sfObjectEventDispatcher.Get()
                            .GetTranslator<sfGameObjectTranslator>(sfType.GameObject);
                        if (translator != null)
                        {
                            translator.TempUnlock(gameObject);
                        }
                    }
                }
            }

            GUI.Label(area, new GUIContent(icon, tooltip));
            if (obj.IsFullyLocked)
            {
                GUI.color = Color.white;
            }
        }

        /**
         * Draws a notification icon for an object.
         * 
         * @param   UObject uobj to draw icon for.
         * @param   sfObject obj for the uobject.
         * @param   Rect area to draw icon in.
         */
        private void DrawNotificationIcon(ksLinkedList<sfNotification> notifications, Rect area)
        {
            string tooltip = null;
            Texture2D icon = ksStyle.GetHelpBoxIcon(MessageType.Warning);
            HashSet<sfNotificationCategory> categories = new HashSet<sfNotificationCategory>();
            foreach (sfNotification notification in notifications)
            {
                if (categories.Add(notification.Category))
                {
                    if (tooltip == null)
                    {
                        tooltip = notification.Category.Name;
                    }
                    else
                    {
                        tooltip += "\n" + notification.Category.Name;
                    }
                }
            }
            GUI.Label(area, new GUIContent(icon, tooltip));
        }

        /**
         * Called when the hierarchy icon offset changes. Refreshes the hierarchy window.
         * 
         * @param   float offset
         */
        private void HandleHierarchyOffsetChange(float offset)
        {
            sfHierarchyWatcher.Get().MarkHierarchyStale();
        }

        /**
         * Called when the project browser icon offset changes. Refreshes the project browser.
         * 
         * @param   Vector2 offset
         */
        private void HandleProjectBrowserOffsetChange(Vector2 offset)
        {
            sfUI.Get().MarkProjectBrowserStale();
        }
    }
}
