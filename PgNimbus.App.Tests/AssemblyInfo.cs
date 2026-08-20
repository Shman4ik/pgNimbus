using Avalonia.Headless;
using PgNimbus.App.Tests;

// Tells Avalonia's headless session which AppBuilder to set the process up
// with. Without it the session builds a default app and our styles, fonts and
// resources are missing, so every view fails to load for the wrong reason.
[assembly: AvaloniaTestApplication(typeof(TestApp))]
