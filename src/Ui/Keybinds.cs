namespace Lightspeed.Ui;

/// <summary>
/// Every key and mouse binding in one table. The Controls tab of the settings menu is drawn from it, so
/// adding a binding means adding a row here.
/// </summary>
public static class Keybinds
{
    public readonly record struct Bind(string Keys, string Action);

    public readonly record struct Group(string Title, Bind[] Binds);

    public static readonly Group[] Groups =
    {
        new("Speed of light", new Bind[]
        {
            new("Scroll up  /  Up", "Faster light"),
            new("Scroll down  /  Down", "Slower light"),
            new("F1", "Toggle the CPU reference solve"),
        }),
        new("Moving (all modes)", new Bind[]
        {
            new("V", "Cycle mode: Fly, Drone, Walk"),
            new("W  A  S  D", "Fly and Walk: move"),
            new("Mouse", "Fly and Walk: look"),
            new("Shift", "Fly and Walk: sprint"),
        }),
        new("Fly", new Bind[]
        {
            new("Space", "Up"),
            new("C", "Down"),
        }),
        new("Walk", new Bind[]
        {
            new("Space", "Jump"),
        }),
        new("Drone (FPV, keyboard and mouse)", new Bind[]
        {
            new("W  /  S", "Throttle up / down, instant; with neither held it sits at 20%, which holds height"),
            new("A  /  D", "Yaw left / right"),
            new("Mouse Y", "Pitch (forward dives; invertible in settings)"),
            new("Mouse X", "Roll"),
        }),
        new("Drone (controller)", new Bind[]
        {
            new("Left stick Y", "Throttle (centre hovers)"),
            new("Left stick X", "Yaw"),
            new("Right stick Y", "Pitch (forward dives)"),
            new("Right stick X", "Roll"),
        }),
        new("Time", new Bind[]
        {
            new("P", "Pause / resume"),
            new(".", "Step one tick (while paused)"),
            new("[  and  ]", "Slow down / speed up time"),
        }),
        new("Physics objects", new Bind[]
        {
            new("Left click", "Throw the selected object"),
            new("B", "Next object: ball, box, cylinder"),
            new("X", "Remove thrown objects"),
        }),
        new("Interface", new Bind[]
        {
            new("Tab", "Settings menu"),
            new("H", "This keybinds list"),
            new("Esc", "Free the mouse (click to lock it again), or close the menu"),
            new("F3", "Cycle debug text: off, simple, full"),
            new("1  2  3", "Debug view: ghosts, rings, labels (full mode)"),
        }),
    };
}
