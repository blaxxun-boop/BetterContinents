﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BetterContinents
{
    public static class UI
    {
        private static Dictionary<string, Action> UICallbacks = new Dictionary<string, Action>();

        private static readonly Color ValheimColor = new Color(1, 0.714f, 0.361f, 1);
        private static Texture BorderTexture;
        private static Texture FrontTexture;
        private static Texture BackTexture;
        private static GUIStyle BigTextStyle;
        private static GUIStyle NormalTextStyle;

        private static bool WindowVisible;
        
        private const int Spacing = 10;
        private const int ButtonHeight = 30;
        private const int ButtonGap = 2;
        private const int ButtonWidth = 150;
        
        private static Rect windowRect = new Rect(ButtonWidth + Spacing * 2, 150, ButtonWidth + Spacing * 2, 500);

        public static void Init()
        {
            // Always reset the UI callbacks on scene change
            SceneManager.activeSceneChanged += (_, __) =>
            {
                ColorTextures.Clear();
                UICallbacks.Clear();
                
                // Only need these on the client
                BorderTexture = CreateFillTexture(Color.Lerp(ValheimColor, Color.white, 0.25f));
                FrontTexture = CreateFillTexture(Color.Lerp(ValheimColor, Color.black, 0.5f));
                BackTexture = CreateFillTexture(Color.Lerp(ValheimColor, Color.black, 0.85f));
                BigTextStyle = null; // We are "resetting" this in-case it got invalidated. We can only actually create it in a GUI function
                NormalTextStyle = null;
                
                UI.Add("Debug Mode", () =>
                {
                    if (BetterContinents.AllowDebugActions)
                    {
                        UI.Text("Better Continents Debug Mode Enabled!", 10, 10, Color.red);

                        if (Menu.IsVisible() || Minimap.instance.m_mode == Minimap.MapMode.Large)
                        {
                            DoDebugMenu();
                        }
                        
                        if (WindowVisible)
                        {
                            DebugUtils.Command.CmdUI.DrawSettingsWindow();
                        }
                    }
                });
                
                UI.Add("Active Hint", () =>
                {
                    //if (Game.instance && Game.instance.WaitingForRespawn())//(Hud.instance?.m_loadingScreen.isActiveAndEnabled ?? false)
                    if(Menu.IsVisible() || Game.instance && Game.instance.WaitingForRespawn())
                    {
                        if(BetterContinents.Settings.EnabledForThisWorld)
                        {
                            UI.DisplayMessage($"<color=gray><size=20><b>{ModInfo.Name} v{ModInfo.Version}</b>: <color=green>ENABLED</color> for this world</size></color>");
                        }
                        else
                        {
                            UI.DisplayMessage($"<color=gray><size=20><b>{ModInfo.Name} v{ModInfo.Version}</b>: <color=#505050ff>DISABLED</color> for this world</size></color>");
                        }
                    }
                });
            };
        }

        private static void DoDebugMenu()
        {
            if (Event.current.type is EventType.KeyUp 
                && Event.current.modifiers is (EventModifiers.Alt | EventModifiers.FunctionKey)
                && Event.current.keyCode is KeyCode.F8)
            {
                WindowVisible = !WindowVisible;
                Event.current.Use();
            }
            if (UI.Button("Better Continents", Spacing, 150))
            {
                WindowVisible = !WindowVisible;
            }
        }
        
        private static void Window(int windowId)
        {
            // Make the windows be draggable.
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
            GUILayout.BeginVertical("Image maps", GUI.skin.window);
            {
                GUILayout.BeginVertical("Height", GUI.skin.window);
                {
                    ShowOptionalButton(
                        BetterContinents.Settings.HasHeightmap || BetterContinents.Settings.HasRoughmap ||
                        BetterContinents.Settings.HasFlatmap, "Reload all height", "reload hm rm fm");
                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.Space(Spacing);
                        GUILayout.BeginVertical();
                        {
                            ShowOptionalButton(BetterContinents.Settings.HasHeightmap, "Reload Heightmap", "reload hm");
                            ShowOptionalButton(BetterContinents.Settings.HasRoughmap, "Reload Roughmap", "reload rm");
                            ShowOptionalButton(BetterContinents.Settings.HasFlatmap, "Reload Flatmap", "reload fm");
                        }
                        GUILayout.EndVertical();
                    }         
                    GUILayout.EndHorizontal();
                }   
                GUILayout.EndVertical();
                ShowOptionalButton(BetterContinents.Settings.HasBiomemap, "Reload Biomemap", "reload bm");
                ShowOptionalButton(BetterContinents.Settings.HasForestmap, "Reload Forestmap", "reload fom");
                ShowOptionalButton(BetterContinents.Settings.HasSpawnmap, "Reload Spawnmap", "reload sm");
            }   
            GUILayout.EndVertical();
            GUILayout.BeginVertical("Locations", GUI.skin.window);
            {
                ShowButton("Show Bosses", "bosses");
                ShowButton("Show All", "show");
                ShowButton("Hide All", "hide");
            }            
            GUILayout.EndVertical();
            GUILayout.BeginVertical("Utils", GUI.skin.window);
            {
                ShowButton("Toggle Minimap Clouds", "clouds");
                ShowButton("Refresh", "refresh");
                ShowButton("Regenerate Locs", "regenloc");
            }
            GUILayout.EndVertical();
        }

        private static void ShowOptionalButton(bool enabled, string name, string command)
        {
            GUI.enabled = enabled;
            if (GUILayout.Button(name, GUILayout.ExpandWidth(false)))
            {
                DebugUtils.RunConsoleCommand("bc " + command);
            }
            GUI.enabled = true;
        }

        private static void ShowButton(string name, string command) => ShowOptionalButton(true, name, command);

        public static void OnGUI()
        {
            foreach (var callback in UICallbacks.Values.ToList())
            {
                callback();
            }
        }

        public static void Add(string key, Action action) => UICallbacks[key] = action;

        public static bool Exists(string key) => UICallbacks.ContainsKey(key);
        
        public static void Remove(string key) => UICallbacks.Remove(key);
        
        private static readonly Dictionary<Color, Texture2D> ColorTextures = new();
        public static Texture2D CreateFillTexture(Color color)
        {
            if (ColorTextures.TryGetValue(color, out var texture) && texture != null)
                return texture;
            texture = new Texture2D(1, 1);
            texture.SetPixels(new []{ color });
            texture.Apply(false);
            ColorTextures[color] = texture;
            return texture;
        }

        public static void ProgressBar(int percent, string text)
        {
            CreateTextStyle();
            
            int yOffs = Screen.height - 75;
            GUI.DrawTexture(new Rect(50 - 4, yOffs - 4, Screen.width - 100 + 8, 50 + 8), BorderTexture, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(50, yOffs, Screen.width - 100, 50), BackTexture, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(50, yOffs, (Screen.width - 100) * percent / 100f, 50), FrontTexture, ScaleMode.StretchToFill);
            GUI.Label(new Rect(75, yOffs, Screen.width - 50, 50), text, BigTextStyle);
        }
        
        public static void DisplayMessage(string msg)
        {
            CreateTextStyle();
            int yOffs = Screen.height - 75;
            GUI.Label(new Rect(75, yOffs, Screen.width - 50, 50), msg, BigTextStyle);
        }

        public static void Text(string msg, int x, int y) => Text(msg, x, y, ValheimColor);

        public static void Text(string msg, int x, int y, Color color)
        {
            CreateTextStyle();
            NormalTextStyle.normal.textColor = color;
            GUI.Label(new Rect(x, y, Screen.width - 50, 50), msg, NormalTextStyle);
        }

        public static bool Button(string label, int x, int y)
        {
            return GUI.Button(new Rect(x, y, ButtonWidth, ButtonHeight), label);
        }

        private static void CreateTextStyle()
        {
            if (BigTextStyle != null)
            {
                return;
            }

            BigTextStyle = new GUIStyle(GUI.skin.label) {fontSize = 40, fontStyle = FontStyle.Bold};
            BigTextStyle.font = Resources.FindObjectsOfTypeAll<Text>()
                .Select(t => t.font)
                .FirstOrDefault(f => f.name == "AveriaSerifLibre-Bold") ?? BigTextStyle.font;
            ;
            // Trying to assign alignment crashes with method not found exception
            // BigTextStyle.alignment = TextAnchor.MiddleCenter;
            BigTextStyle.normal.textColor = Color.Lerp(ValheimColor, Color.white, 0.75f);

            NormalTextStyle = new GUIStyle(BigTextStyle) {fontSize = 20, fontStyle = FontStyle.Normal};
        }
    }
}