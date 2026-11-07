using Godot;

namespace Lightspeed.Ui;

/// <summary>Shared look for the menu and the HUD: dark glass panels with one orange accent.</summary>
public static class UiTheme
{
    public static readonly Color Accent = new(1f, 0.62f, 0.25f);
    public static readonly Color PanelColor = new(0.04f, 0.05f, 0.08f, 0.92f);
    public static readonly Color Control = new(0.13f, 0.15f, 0.21f);
    public static readonly Color ControlHover = new(0.19f, 0.22f, 0.30f);
    public static readonly Color Text = new(0.92f, 0.93f, 0.96f);
    public static readonly Color Dim = new(0.62f, 0.66f, 0.74f);

    public static StyleBoxFlat Box(Color fill, int radius = 6, int marginX = 10, int marginY = 5, Color? border = null)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            ContentMarginLeft = marginX, ContentMarginRight = marginX,
            ContentMarginTop = marginY, ContentMarginBottom = marginY,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        };
        if (border is { } b)
        {
            box.BorderColor = b;
            box.SetBorderWidthAll(1);
        }
        return box;
    }

    public static Theme Create()
    {
        var theme = new Theme();
        theme.DefaultFontSize = 14;

        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(Control));
            theme.SetStylebox("hover", type, Box(ControlHover));
            theme.SetStylebox("pressed", type, Box(Accent with { A = 0.35f }));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Colors.White);
        }

        theme.SetStylebox("hover", "CheckButton", Box(new Color(1, 1, 1, 0.05f), 4, 4, 4));
        theme.SetStylebox("normal", "CheckButton", Box(Colors.Transparent, 4, 4, 4));
        theme.SetStylebox("pressed", "CheckButton", Box(Colors.Transparent, 4, 4, 4));
        theme.SetStylebox("focus", "CheckButton", new StyleBoxEmpty());
        theme.SetColor("font_color", "CheckButton", Text);
        theme.SetColor("font_pressed_color", "CheckButton", Colors.White);
        theme.SetColor("font_hover_color", "CheckButton", Colors.White);
        theme.SetColor("font_hover_pressed_color", "CheckButton", Colors.White);

        theme.SetStylebox("slider", "HSlider", Box(new Color(1, 1, 1, 0.12f), 3, 0, 3));
        theme.SetStylebox("grabber_area", "HSlider", Box(Accent with { A = 0.85f }, 3, 0, 3));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, 3, 0, 3));

        theme.SetStylebox("panel", "TabContainer", new StyleBoxEmpty());
        theme.SetStylebox("tab_selected", "TabContainer", Box(Accent with { A = 0.22f }, 5, 9, 5));
        theme.SetStylebox("tab_hovered", "TabContainer", Box(new Color(1, 1, 1, 0.08f), 5, 9, 5));
        theme.SetStylebox("tab_unselected", "TabContainer", Box(Colors.Transparent, 5, 9, 5));
        theme.SetColor("font_selected_color", "TabContainer", Colors.White);
        theme.SetColor("font_hovered_color", "TabContainer", Colors.White);
        theme.SetColor("font_unselected_color", "TabContainer", Dim);

        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color(1, 1, 1, 0.1f), Thickness = 1 });
        theme.SetColor("font_color", "Label", Text);
        return theme;
    }
}
