# Hasil migrasi Collector OPC UA

Tanggal: 20 November 2026. Source dasar: [rms-collector-net](https://github.com/arhan321/rms-collector-net/tree/63e9b1a5384d173b9cbee818fe4e8e9808e362ad).

## Cakupan

Executable baru `rms_collector_opcua` mengganti jalur pengiriman Collector → HTTP API dengan penyediaan tag melalui OPC UA. Pembacaan robot tetap menggunakan kelas YMConnect asli. Ignition bertindak sebagai client untuk server di PC Collector.

Konfigurasi ada di `config/collector.opcua.conf` dan `config/robots.opcua.conf`. Proyek API lama tetap berada di source untuk kebutuhan transisi. Proyek baru tidak mengompilasi `src-csharp/Program.cs` atau `src-csharp/Transport.cs` sehingga tidak menjalankan HTTP transport maupun spool lama.

## Hasil verifikasi

| Pemeriksaan | Hasil |
| --- | --- |
| Build/publish Release Windows x64, .NET 10 | Berhasil |
| Format executable hasil publish | PE32+ Windows x86-64 |
| Tes integrasi OPC UA | 25 pemeriksaan lulus |
| Tes Collector asli | 14 tes lulus |
| Pemeriksaan paket NuGet termasuk dependency transitif | Tidak dilaporkan vulnerable pada sumber yang tersedia saat pemeriksaan |
| `git diff --check` | Lulus |
| Tes dengan controller Yaskawa fisik | Belum dilakukan |
| Tes pada Gateway Ignition pengguna | Belum dilakukan |
| Integrasi Ignition → tabel Laravel | Belum diimplementasikan pada tahap ini |

Tes OPC membuka server dan client melalui jaringan loopback. Certificate publik ditukar secara eksplisit, client yang belum dipercaya ditolak, kemudian koneksi terenkripsi dibuat. Tes mencakup browse dua robot, subscription perubahan nilai, pemetaan torque/pulse/job/operating/I/O/alarm, pemisahan identitas robot, penolakan write, data sebelum pembacaan pertama, kegagalan baca, data non-finite, stale, disconnect dan recovery. Nilai robot pada tes berasal dari MockReader.

## Perbedaan operasional yang perlu dipahami

OPC UA memberikan nilai terbaru melalui read/subscription. Tidak ada jaminan replay seluruh sampel selama Ignition tidak terhubung dan tidak ada OPC historical-read dalam Collector ini. Penyimpanan histori dan pencegahan duplikasi alarm perlu diterapkan di sisi integrasi Ignition/SQL.

Nama job berasal dari task Master. Group R1 dan maksimum 8 controller mengikuti batas pembaca asli. Data torque tetap dalam satuan `sdk_raw`. Migrasi transport tidak menambahkan diagnosis OEM Speed Reducer Life, ampere aktual, atau position error.

Baca `PANDUAN_IGNITION_OPCUA.md` untuk penempatan file, build, pengaturan endpoint, beberapa robot, certificate trust, struktur tag dan langkah uji DOJO.
