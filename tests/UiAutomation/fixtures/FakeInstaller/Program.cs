// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

// Synthetic installer used only by the UI-automation checks. It records when it started and
// finished, then sleeps so an install queue can be observed from the outside. It installs
// nothing and touches nothing except the marker file named by FAKE_INSTALLER_MARKER.

var marker = Environment.GetEnvironmentVariable("FAKE_INSTALLER_MARKER");
if (string.IsNullOrWhiteSpace(marker))
{
    marker = Path.Combine(Path.GetTempPath(), "fake-installer.log");
}

var sleepText = Environment.GetEnvironmentVariable("FAKE_INSTALLER_SLEEP_MS");
var sleepMs = int.TryParse(sleepText, out var parsed) ? parsed : 2000;

var label = Environment.GetEnvironmentVariable("FAKE_INSTALLER_LABEL") ?? Path.GetFileName(Environment.ProcessPath) ?? "fake";
var line = string.Join(' ', args);

Append(marker, $"START|{label}|{DateTimeOffset.UtcNow:O}|pid={Environment.ProcessId}|args={line}");
await Task.Delay(sleepMs);
Append(marker, $"END|{label}|{DateTimeOffset.UtcNow:O}|pid={Environment.ProcessId}");
return 0;

static void Append(string path, string text)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            File.AppendAllText(path, text + Environment.NewLine);
            return;
        }
        catch (IOException)
        {
            Thread.Sleep(50);
        }
    }
}
