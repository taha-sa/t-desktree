# AmberTree — Proje Talimatları

Genel çalışma biçimi (koordinatör, Sonnet alt agentlar, esnek plan, sapma kuralı) global
`C:\Users\hp\.claude\CLAUDE.md` içinde. Burada yalnızca bu projeye özel kararlar var.

## Kullanıcının bu projede verdiği kararlar

- **Ekran görüntüsü alma.** Görsel doğrulama, pencere yakalama, screenshot döngüsü yok.
  Sadece koddan çalış: build, kısa çalıştırmada exception/binding çıktısı, grep kontrolleri.
  Görsel kontrolü kullanıcı kendisi yapar. Alt agent brief'lerine bunu açıkça yaz.
- **Uzun süren tek parça faz istemiyor.** İşi 10-15 dakikalık küçük parçalara böl,
  her parça kendi raporunu versin. Dosya çakışması yoksa parçaları paralel çalıştır.
- **Kısa konuş.** Plan dışında uzun cümle yok.
- **Görsel kimlik:** "Graphite / Aurora" — eski amber sarı/siyah bırakıldı. Uygulama adı "AmberTree" kalır.
- **Kapsam:** UI + görünür UX hataları. Tarama algoritması (ScanEngine, NativeFileSystem) değiştirilmez.
- **Dil:** arayüz Türkçe (karar koordinatöre bırakılmıştı).

## Proje gerçekleri

- WPF, net10.0-windows, NuGet yok, elle MVVM.
- Kısayol: `C:\Users\hp\Desktop\araçlar\AmberTree.lnk` → `bin\Release\publish\AmberTree.exe`.
  Değişiklik sonrası: `dotnet publish -c Release -o bin\Release\publish` (kısayol bunu açar).
- Git: `baseline` = yeniden tasarım öncesi. Her faz ayrı commit. Yedek: `..\AmberTree_yedek_2026-11-01`.
- Tasarım sistemi ve VM sözleşmesi: `C:\Users\hp\Desktop\claude_Projem_05\plans\ambertree-redesign.md`
  ve `ambertree-faz1-teslim.md`. Yeni kaynak/komut adı uydurmadan önce bunlara bak.
- Renkler yalnızca `Themes\Tokens.*.xaml` token'larıyla (DynamicResource). Hex ve emoji yasak.
- Boyutlar converter ile değil `*Display` property'leriyle bağlanır (birim değişimi ancak böyle yenilenir).

## Açık kalanlar (yeniden tasarım sonrası)

- Görsel hiç doğrulanmadı: kolon hizası, 900px düzen, açık tema, Fluent glyph'leri (Tree, Treemap, Extensions, TopList, Error).
- Sağ tık / silme / klavye kısayolları gerçek kullanımda denenmedi.
- DialogService sistem MessageBox kullanıyor (temasız). Aramada debounce yok.
- Uzantılar ve Top 100'de kategori renkli ikon yok.
