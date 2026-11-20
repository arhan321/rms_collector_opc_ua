# Revisi OPC UA Only v2 — 20 November 2026

Alur: **Yaskawa → Collector C# / YMConnect → OPC UA → Ignition → Database**.

## Perubahan utama

- `Configuration.cs` hanya berisi pengaturan robot, I/O dan interval polling. Konfigurasi URL API, token, HTTP timeout dan spool dihapus.
- `Program.cs` menjadi entry point server OPC UA. Hanya `RmsCollector.csproj` yang menjadi proyek executable utama.
- Source sender HTTP dan contoh konfigurasi lama dihapus dari project.
- `Models.cs` menggunakan `SampleJson` untuk serialisasi tag snapshot. Endpoint dan serializer request lama dihapus.
- Pembaca YMConnect dan pemetaan tag disesuaikan dengan helper baru; Node ID dan format JSON snapshot tetap sama.
- Job polling ditentukan melalui `.conf`, tanpa environment fallback.
- Build, publish, launcher, tes dan dokumentasi diarahkan ke satu proyek OPC UA.
- Paket perubahan memuat isi lengkap file berubah dan alat untuk memindahkan file lama ke backup di luar project.

## Verifikasi

Tes integrasi OPC UA: **32 pemeriksaan lulus**, termasuk penolakan pengaturan transport lama, browse dua robot, secure session dengan certificate trust, subscription, pemetaan nilai, read failure, stale/recovery, isolasi robot dan penolakan write. Data robot dalam tes berasal dari simulasi.

Build Release dan publish Windows x64 diverifikasi. Kode pengiriman HTTP tidak ditemukan pada source executable. Pengujian Windows fisik, Gateway Ignition pengguna dan robot DOJO belum dilakukan.

Ignition perlu dikonfigurasi untuk menyimpan tag ke database. Paket Collector tidak memasang koneksi database, tidak membuat tabel pada database pengguna, dan tidak menulis data langsung ke SQL Server.

Baca `PANDUAN_IGNITION_OPCUA.md` untuk koneksi Ignition dan `MIGRATION.md` untuk penempatan perubahan dari paket sebelumnya.
