using System;
using UnityEngine;
namespace NPCAI
{
    internal static class DebugOverlay
    {
        private static Texture2D _white;
        private static GUIStyle _floatStyle, _lineStyle;
        private static void EnsureStyles()
        {
            if (_white != null) return;
            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _white.SetPixel(0, 0, Color.white); _white.Apply();
            _floatStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _lineStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontStyle = FontStyle.Bold };
        }

        // debug helpers (Senses ghosts): a diamond and a label at a world point
        internal static void Mark(Vector3 world, Color c, float size)
        {
            var cam = Camera.main; if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(world); if (sp.z <= 0f) return;
            EnsureStyles();
            Vector2 centre = new Vector2(sp.x, Screen.height - sp.y);
            var old = GUI.color; GUI.color = c;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, centre);
            GUI.DrawTexture(new Rect(centre.x - size * 0.5f, centre.y - size * 0.5f, size, size), _white);
            GUI.matrix = m;
            GUI.color = old;
        }

        internal static void Label(Vector3 world, string text, Color c)
        {
            var cam = Camera.main; if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(world); if (sp.z <= 0f) return;
            EnsureStyles();
            _floatStyle.fontSize = 12;
            DrawText(new Rect(sp.x - 200f, Screen.height - sp.y - 10f, 400f, 20f), text, _floatStyle, c);
        }


        public static void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            try { Senses.DrawDebug(); } catch (Exception e) { Plugin.Log.LogError("Senses overlay: " + e); }
            try { Idle.DrawDebug(); } catch (Exception e) { Plugin.Log.LogError("Idle overlay: " + e); }
            try { Nav.DrawDebug(); } catch (Exception e) { Plugin.Log.LogError("Nav overlay: " + e); }
        }
        private static void DrawText(Rect r, string text, GUIStyle style, Color c)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, c.a * 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);    // shadow
            GUI.color = c;
            GUI.Label(r, text, style);
            GUI.color = old;
        }
    
    }
}
