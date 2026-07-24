using Microsoft.Playwright;

namespace Aspire.Hosting.Testing.Videos;

/// <summary>
/// Wraps a Playwright IPage with cinematic interactions:
/// smooth mouse movement, visual click indicator, typewriter-style input,
/// thinking pauses, and narrator integration.
/// Controlled by CINEMATIC=true environment variable.
/// </summary>
public class CinematicPage
{
    private readonly IPage _page;
    private readonly VideoNarrator _narrator;
    private readonly bool _cinematic;

    // Timing (cinematic mode)
    private const int TypeDelayMs = 60;       // ms between keystrokes
    private const int ThinkShortMs = 800;     // short pause (within a form)
    private const int ThinkMediumMs = 1500;   // medium pause (between fields)
    private const int ThinkLongMs = 2500;     // long pause (between major steps)
    private const int MouseMoveSteps = 30;    // steps for smooth mouse glide
    private const int PostClickMs = 500;      // pause after each click

    // Timing (fast mode)
    private const int FastPauseMs = 300;

    public CinematicPage(IPage page, VideoNarrator narrator, bool cinematic = false)
    {
        _page = page;
        _narrator = narrator;
        _cinematic = cinematic;
    }

    /// <summary>The underlying Playwright page, for advanced interactions.</summary>
    public IPage Page => _page;

    public static bool IsCinematicMode()
        => string.Equals(Environment.GetEnvironmentVariable("CINEMATIC"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Narrator says something (recorded with timestamp).
    /// In cinematic mode, waits for the TTS audio duration so the video
    /// stays in sync with the voiceover. In fast mode, no wait.
    /// </summary>
    public async Task NarrateAsync(string text)
    {
        var durationMs = _narrator.Say(text);
        if (_cinematic && durationMs > 0)
        {
            await _page.WaitForTimeoutAsync(durationMs);
        }
    }

    /// <summary>Navigate to a URL with a settling pause.</summary>
    public async Task GoToAsync(string url)
    {
        await _page.GotoAsync(url);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await PauseAsync(ThinkMediumMs);
    }

    /// <summary>Type into a field character by character (cinematic) or fill instantly (fast).</summary>
    public async Task TypeAsync(string selector, string text)
    {
        var locator = _page.Locator(selector);
        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        if (_cinematic)
        {
            await MoveToAsync(locator);
            await ShowClickPulseAsync(locator);
            await locator.ClickAsync();
            await _page.WaitForTimeoutAsync(PostClickMs);
            await locator.PressSequentiallyAsync(text, new() { Delay = TypeDelayMs });
        }
        else
        {
            await locator.ClickAsync();
            await locator.FillAsync(text);
        }
        await PauseAsync(ThinkShortMs);
    }

    /// <summary>Click a button by role and name.</summary>
    public async Task ClickButtonAsync(string name)
    {
        var locator = _page.GetByRole(AriaRole.Button, new() { Name = name }).First;
        await ClickLocatorAsync(locator);
        await PauseAsync(ThinkShortMs);
    }

    /// <summary>Click a link by role and name.</summary>
    public async Task ClickLinkAsync(string name)
    {
        var locator = _page.GetByRole(AriaRole.Link, new() { Name = name }).First;
        await ClickLocatorAsync(locator);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await PauseAsync(ThinkMediumMs);
    }

    /// <summary>Click a submit button matching text.</summary>
    public async Task SubmitAsync(string buttonText)
    {
        var locator = _page.Locator($"button[type='submit']:has-text('{buttonText}')");
        await ClickLocatorAsync(locator);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await PauseAsync(ThinkMediumMs);
    }

    /// <summary>Click by visible text.</summary>
    public async Task ClickTextAsync(string text)
    {
        var locator = _page.GetByText(text);
        await ClickLocatorAsync(locator);
        await PauseAsync(ThinkShortMs);
    }

    /// <summary>Take a screenshot.</summary>
    public async Task ScreenshotAsync(string path, bool fullPage = false)
    {
        await _page.ScreenshotAsync(new() { Path = path, FullPage = fullPage });
    }

    /// <summary>Simulate user thinking between major steps.</summary>
    public async Task ThinkAsync()
    {
        await PauseAsync(ThinkLongMs);
    }

    private async Task ClickLocatorAsync(ILocator locator)
    {
        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        if (_cinematic)
        {
            await MoveToAsync(locator);
            await ShowClickPulseAsync(locator);
        }
        await locator.ClickAsync();
        if (_cinematic)
            await _page.WaitForTimeoutAsync(PostClickMs);
    }

    private async Task MoveToAsync(ILocator locator)
    {
        await EnsureCursorInjectedAsync();

        var box = await locator.BoundingBoxAsync();
        if (box == null) return;

        var targetX = box.X + box.Width / 2;
        var targetY = box.Y + box.Height / 2;

        await _page.Mouse.MoveAsync(targetX, targetY, new() { Steps = MouseMoveSteps });
        await _page.EvaluateAsync($"window.__moveCursor({targetX}, {targetY})");
        await _page.WaitForTimeoutAsync(150);
    }

    private async Task ShowClickPulseAsync(ILocator locator)
    {
        var box = await locator.BoundingBoxAsync();
        if (box == null) return;

        var x = box.X + box.Width / 2;
        var y = box.Y + box.Height / 2;
        await _page.EvaluateAsync($"window.__clickPulse({x}, {y})");
    }

    private async Task EnsureCursorInjectedAsync()
    {
        var injected = await _page.EvaluateAsync<bool>("() => !!window.__cursorInjected");
        if (injected) return;

        await _page.EvaluateAsync(@"() => {
            const cursor = document.createElement('div');
            cursor.id = '__pw-cursor';
            Object.assign(cursor.style, {
                position: 'fixed', zIndex: '2147483647',
                width: '20px', height: '20px', borderRadius: '50%',
                background: 'rgba(59, 130, 246, 0.7)',
                border: '2px solid rgba(255, 255, 255, 0.9)',
                boxShadow: '0 0 8px rgba(59, 130, 246, 0.5)',
                pointerEvents: 'none',
                transform: 'translate(-50%, -50%)',
                transition: 'left 0.08s ease-out, top 0.08s ease-out',
                left: '-100px', top: '-100px',
            });
            document.body.appendChild(cursor);

            const style = document.createElement('style');
            style.textContent = `
                @keyframes __pw-pulse {
                    0%   { transform: translate(-50%, -50%) scale(0.3); opacity: 0.8; }
                    100% { transform: translate(-50%, -50%) scale(2.5); opacity: 0; }
                }
                .__pw-click-pulse {
                    position: fixed; z-index: 2147483646;
                    width: 30px; height: 30px; border-radius: 50%;
                    border: 3px solid rgba(59, 130, 246, 0.6);
                    pointer-events: none;
                    animation: __pw-pulse 0.5s ease-out forwards;
                }
            `;
            document.head.appendChild(style);

            window.__moveCursor = (x, y) => {
                cursor.style.left = x + 'px';
                cursor.style.top = y + 'px';
            };
            window.__clickPulse = (x, y) => {
                const ring = document.createElement('div');
                ring.className = '__pw-click-pulse';
                ring.style.left = x + 'px';
                ring.style.top = y + 'px';
                document.body.appendChild(ring);
                setTimeout(() => ring.remove(), 600);
            };
            window.__cursorInjected = true;
        }");
    }

    private async Task PauseAsync(int cinematicMs)
    {
        var ms = _cinematic ? cinematicMs : FastPauseMs;
        await _page.WaitForTimeoutAsync(ms);
    }
}
