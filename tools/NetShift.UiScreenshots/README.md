# NetShift UI screenshots

Render the production WPF pages to PNG without starting `NetShift.exe`:

```powershell
dotnet run --project tools/NetShift.UiScreenshots -- artifacts/ui-screenshots
```

The preview suppresses motion so every PNG captures a stable, fully rendered state.

Pass `basic`, `dns`, `zapret`, or `settings` after the output directory to render one page. The preview uses fixed sample data and skips saved settings, adapter discovery, Windows service queries, registry checks, network requests, startup reconciliation, and update actions. The regular application constructor keeps its normal startup behavior.
