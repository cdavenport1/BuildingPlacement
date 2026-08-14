using UnityEngine;

namespace NuclearOptionCommander;

internal static class CommanderUiTheme
{
    private static bool initialized;
    private static Texture2D? panelTexture;
    private static Texture2D? raisedTexture;
    private static Texture2D? buttonTexture;
    private static Texture2D? buttonHoverTexture;
    private static Texture2D? buttonActiveTexture;
    private static Texture2D? accentTexture;
    private static Texture2D? warningTexture;
    private static Texture2D? borderTexture;

    internal static GUIStyle Window { get; private set; } = null!;
    internal static GUIStyle Panel { get; private set; } = null!;
    internal static GUIStyle Button { get; private set; } = null!;
    internal static GUIStyle PrimaryButton { get; private set; } = null!;
    internal static GUIStyle DangerButton { get; private set; } = null!;
    internal static GUIStyle SelectedButton { get; private set; } = null!;
    internal static GUIStyle Label { get; private set; } = null!;
    internal static GUIStyle MutedLabel { get; private set; } = null!;
    internal static GUIStyle Header { get; private set; } = null!;
    internal static GUIStyle Money { get; private set; } = null!;
    internal static GUIStyle HelpButton { get; private set; } = null!;

    internal static void Ensure()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        panelTexture = MakeTexture(new Color(0.03f, 0.045f, 0.06f, 0.97f));
        raisedTexture = MakeTexture(new Color(0.08f, 0.12f, 0.14f, 0.98f));
        buttonTexture = MakeTexture(new Color(0.12f, 0.17f, 0.18f, 1f));
        buttonHoverTexture = MakeTexture(new Color(0.18f, 0.25f, 0.27f, 1f));
        buttonActiveTexture = MakeTexture(new Color(0.22f, 0.46f, 0.45f, 1f));
        accentTexture = MakeTexture(new Color(0.14f, 0.38f, 0.37f, 1f));
        warningTexture = MakeTexture(new Color(0.5f, 0.18f, 0.15f, 1f));
        borderTexture = MakeTexture(new Color(0.32f, 0.72f, 0.7f, 0.95f));

        Label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.9f, 0.94f, 0.93f) },
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft
        };
        MutedLabel = new GUIStyle(Label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.62f, 0.7f, 0.7f) }
        };
        Header = new GUIStyle(Label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.46f, 0.96f, 0.92f, 1f) },
            alignment = TextAnchor.MiddleLeft
        };
        Window = new GUIStyle(GUI.skin.window)
        {
            normal = { background = panelTexture, textColor = Color.white },
            onNormal = { background = panelTexture, textColor = Color.white },
            border = new RectOffset(2, 2, 2, 2),
            padding = new RectOffset(10, 10, 30, 10),
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        Panel = new GUIStyle(GUI.skin.box)
        {
            normal = { background = raisedTexture, textColor = Label.normal.textColor },
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(8, 8, 8, 8)
        };
        Button = new GUIStyle(GUI.skin.button)
        {
            normal = { background = buttonTexture, textColor = Label.normal.textColor },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = buttonActiveTexture, textColor = Color.white },
            focused = { background = buttonHoverTexture, textColor = Color.white },
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(7, 7, 4, 4),
            fontSize = 11,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        PrimaryButton = new GUIStyle(Button)
        {
            normal = { background = accentTexture, textColor = Color.white },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = buttonActiveTexture, textColor = Color.white },
            fontStyle = FontStyle.Bold
        };
        Money = new GUIStyle(PrimaryButton)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(12, 12, 4, 4),
            alignment = TextAnchor.MiddleCenter
        };
        DangerButton = new GUIStyle(Button)
        {
            normal = { background = warningTexture, textColor = Color.white },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = buttonActiveTexture, textColor = Color.white }
        };
        SelectedButton = new GUIStyle(Button)
        {
            normal = { background = buttonActiveTexture, textColor = Color.white },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = accentTexture, textColor = Color.white },
            fontStyle = FontStyle.Bold
        };
        HelpButton = new GUIStyle(Button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 0, 0)
        };
    }

    internal static Rect ClampWindow(Rect rect, float margin = 12f)
    {
        rect.width = Mathf.Min(rect.width, Mathf.Max(160f, CommanderUiScale.Width - margin * 2f));
        rect.height = Mathf.Min(rect.height, Mathf.Max(120f, CommanderUiScale.Height - margin * 2f));
        rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, CommanderUiScale.Width - rect.width - margin));
        rect.y = Mathf.Clamp(rect.y, margin, Mathf.Max(margin, CommanderUiScale.Height - rect.height - margin));
        return rect;
    }

    internal static void DrawFrame(Rect rect, float thickness = 2f)
    {
        Ensure();
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), borderTexture!);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), borderTexture!);
    }

    internal static bool DrawHelpButton(float windowWidth, ref bool visible)
    {
        Ensure();
        if (GUI.Button(new Rect(windowWidth - 64f, 3f, 26f, 22f), "?", HelpButton))
        {
            visible = !visible;
        }

        return visible;
    }

    internal static void DrawHelpOverlay(Rect rect, string text)
    {
        Ensure();
        GUI.Box(rect, string.Empty, Panel);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f), text, Label);
    }

    private static Texture2D MakeTexture(Color color)
    {
        Texture2D texture = new(1, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        texture.SetPixel(0, 0, color);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }
}
