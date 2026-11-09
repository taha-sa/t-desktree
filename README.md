# T-DeskTree (AmberTree)

WPF disk alanı analizi (Graphite / Aurora). Sürüm 1.1.0.

## Derleme
T-Suite SDK'ya (`TSuite.Link`, `TSuite.Update`, `TSuite.Updater`) **ProjectReference** ile bağımlıdır. Kardeş depo `C:\Users\hp\Projects\t-suite`
konumunda olmalıdır (csproj: `$(MSBuildProjectDirectory)\..\..\..\..\Projects\t-suite`). Başka konum: `dotnet build -p:TSuiteRoot=D:\yol\t-suite`.

- `dotnet build` · tanılama: `T-DeskTree.exe --test`
- T-Update: açılıştan 30 sn sonra arka plan kontrolü; XAML `UpdateBanner` (Views/). T-Link: `t-desktree` (scan.finished, disk.low; disk.status, scan.start, app.show, app.version).
- Sürüm: `release.ps1 <sürüm> "notlar"` (`-Publish` verilmedikçe yayınlamaz; anahtar `C:\Users\hp\.t-suite\keys\t-desktree.pem`).
