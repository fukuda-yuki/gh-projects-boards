using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GhProjectsBoards.App.GridPrototype;

internal static class GridInputTrace
{
    // Opt-in synthetic-grid diagnostics. Buffer observations until the window closes:
    // logging must not drain the Dispatcher, force layout, change focus, or handle input.
    internal static void Attach(Window window, DataGrid grid, string scenario, Func<int>? history = null)
    {
        var directory = Environment.GetEnvironmentVariable("GHPB_GRID_INPUT_TRACE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var observations = new List<object>();
        var started = Stopwatch.GetTimestamp();
        void Record(string name, RoutedEventArgs args)
        {
            if (observations.Count >= 10000) return;
            var key = args as KeyEventArgs;
            var composition = args as TextCompositionEventArgs;
            var focus = Keyboard.FocusedElement;
            observations.Add(new
            {
                sequence = observations.Count,
                milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                name, args.Handled,
                source = args.OriginalSource?.GetType().Name,
                key = key?.Key.ToString(), imeKey = key?.ImeProcessedKey.ToString(),
                text = composition?.Text, composition = composition?.TextComposition.CompositionText,
                focus = focus?.GetType().Name, editorText = (focus as TextBox)?.Text,
                row = grid.CurrentCell.Item?.ToString(), column = grid.CurrentColumn?.DisplayIndex,
                committed = grid.CurrentCell.Item is GridPrototypeRow row ? row.Title : null,
                undoCount = history?.Invoke()
            });
        }
        foreach (var routedEvent in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            window.AddHandler(routedEvent, new KeyEventHandler((_, e) => Record(e.RoutedEvent.Name, e)), true);
        foreach (var routedEvent in new[] { TextCompositionManager.PreviewTextInputStartEvent,
            TextCompositionManager.PreviewTextInputUpdateEvent, TextCompositionManager.PreviewTextInputEvent,
            TextCompositionManager.TextInputEvent })
            window.AddHandler(routedEvent, new TextCompositionEventHandler((_, e) => Record(e.RoutedEvent.Name, e)), true);
        window.AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => Record("GotKeyboardFocus", e)), true);
        grid.BeginningEdit += (_, e) => Record("BeginningEdit", e.EditingEventArgs ?? new RoutedEventArgs());
        grid.PreparingCellForEdit += (_, e) => Record("PreparingCellForEdit", e.EditingEventArgs ?? new RoutedEventArgs());
        grid.CellEditEnding += (_, e) => Record($"CellEditEnding:{e.EditAction}", new RoutedEventArgs());
        window.Closed += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"{scenario}-{Guid.NewGuid():N}.json"),
                    JsonSerializer.Serialize(new { scenario, runtime = Environment.Version.ToString(), observations },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { /* Optional diagnostics must not prevent ordinary window shutdown. */ }
            catch (UnauthorizedAccessException) { }
        };
    }
}
