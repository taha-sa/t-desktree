# T-DeskTree (AmberTree)

WPF disk alanı analizi (Graphite / Aurora). Sürüm 1.2.0.

## Dil ve yasal bilgiler
TR/EN/DE arayüz; İngilizce nötr kaynaklar `Localization/Strings.resx` içindedir.
Hakkında ekranından sistem dili, Türkçe, English veya Deutsch seçilir. Değişiklik
yeniden başlatmada uygulanır; Windows bölgesel biçimleri sistem modunda korunur.
T-Bridge daveti her seferinde yeniden açılabilir; kuruluysa T-Bridge açılır.
Hakkında → Yasal, gömülü MIT lisansını, TR/EN/DE gizlilik metnini ve açık kaynak
bildirimlerini ağ bağlantısı olmadan gösterir.

`dotnet run --project tests/AmberTree.Checks -c Release`: kaynak anahtarları,
biçim argümanları, üç dilde WPF görünüm oluşturma, İngilizce yedek dil,
yasal belgeler ve zorunlu güncellemenin ertelenememesi. Pencere göstermez,
tarama/silme/IPC başlatmaz ve kullanıcı ayarlarını değiştirmez.

## Derleme
T-Suite SDK'ya (`TSuite.Link`, `TSuite.Update`, `TSuite.Updater`) **ProjectReference** ile bağımlıdır. Kardeş depo `C:\Users\hp\Projects\t-suite`
konumunda olmalıdır (csproj: `$(MSBuildProjectDirectory)\..\..\..\..\Projects\t-suite`). Başka konum: `dotnet build -p:TSuiteRoot=D:\yol\t-suite`.

- `dotnet build` · tanılama: `T-DeskTree.exe --test`
- T-Update: açılıştan 30 sn sonra arka plan kontrolü; XAML `UpdateBanner` (Views/). T-Link: `t-desktree` (scan.finished, disk.low; disk.status, scan.start, app.show, app.version).
- Sürüm: `release.ps1 <sürüm> "notlar"` (`-Publish` verilmedikçe yayınlamaz; anahtar `C:\Users\hp\.t-suite\keys\t-desktree.pem`).
- Güncelleme komutları: `app.update.check` / `app.update.install` (T-Bridge "Hepsini güncelle"); kapalıyken `T-DeskTree.exe --update` (arayüzsüz: kontrol, varsa indir + güncelleyici, çık).

`--test` tarama/istatistik denetimi kullanıcı belgeleri yerine geçici bir örnek
klasör kullanır ve istatistikleri uygulamadaki `TreeMaintenance` yoluyla hesaplar.

## CI / Yayın
- Kararlı sürüm için `git tag v1.2.0 && git push --tags` komutlarını kullanın.
- Beta sürümleri `v1.2.0-beta.1` biçiminde etiketlenir ve beta kanalında yayınlanır.
- Yayın işi, imzalama ve yayınlama öncesinde `release` ortamındaki onayı bekler.
