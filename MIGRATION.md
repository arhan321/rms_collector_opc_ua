# Memperbarui paket sebelumnya ke OPC UA Only v2

Paket v2 ini melanjutkan `RMS_OPCUA_FULL_SOURCE.zip` yang sebelumnya diberikan. Source pengiriman lama dibersihkan dan proyek utama menjadi satu executable OPC UA.

## Pilihan 1 — memakai full source baru

1. Hentikan Collector lama.
2. Ekstrak `RMS_OPCUA_ONLY_FULL_SOURCE_V2.zip` ke folder baru.
3. Salin konfigurasi robot dan endpoint milik sendiri dari dua file `.conf` sebelumnya, serta folder `pki-opcua` jika ingin mempertahankan certificate pada PC yang sama. Jangan membagikan private key.
4. Jalankan `scripts\test.cmd`, kemudian `scripts\publish-opcua.cmd`.
5. Jalankan aplikasi dari `publish\opcua`. Setelah koneksi benar, aktifkan logging database di Ignition.

## Pilihan 2 — memakai file perubahan saja

1. Hentikan Collector yang berjalan.
2. Buat salinan folder project saat ini sebagai backup sebelum menimpa file.
3. Simpan nilai endpoint dan `pki_dir` milik sendiri dari `config/collector.opcua.conf`. Paket perubahan memuat versi file konfigurasi itu dengan komentar yang diperbarui. `config/robots.opcua.conf` tidak berubah dari paket sebelumnya dan tidak ditimpa oleh ZIP perubahan.
4. Ekstrak `RMS_OPCUA_ONLY_CHANGED_FILES_V2.zip` tepat ke root project, sejajar `RmsCollector.csproj`. Pilih replace untuk file yang berubah.
5. Kembalikan nilai endpoint/path sesuai konfigurasi PC sendiri.
6. Jalankan `BERSIHKAN_FILE_LAMA.cmd` dari root sebelum build. File lama dalam `FILE_YANG_DIHAPUS.txt` dan folder hasil build lama dipindahkan ke folder backup di sebelah project. Source lama tidak lagi berada di dalam project. Konfigurasi OPC UA, konfigurasi robot, SDK dan folder certificate tidak dipindah.
7. Jalankan `scripts\test.cmd`, lalu `scripts\publish-opcua.cmd`.
8. Jalankan executable/launcher baru dari `publish\opcua`; jangan gunakan binary lama dari folder lain.

ZIP tidak bisa menghapus file ketika hanya diekstrak, sehingga langkah pembersihan diperlukan untuk pembaruan di folder lama. Jika script tidak bisa berjalan, pindahkan secara manual file yang tercantum dalam daftar ke folder backup di luar project. Script Windows belum dijalankan pada PC pengguna; jika ada file terkunci, hentikan proses Collector dan ulangi.

## Perubahan lokasi

| Sebelumnya | Sekarang |
| --- | --- |
| Proyek OPC UA terpisah di `opcua/` | Proyek utama `RmsCollector.csproj` |
| Startup OPC UA di `opcua/OpcUaProgram.cs` | Startup tunggal di `src-csharp/Program.cs` |
| Model snapshot memakai nama `ApiPayloads` | Helper JSON bernama `SampleJson` |
| Tes lama dan tes OPC UA terpisah | `scripts\test.cmd` menjalankan tes OPC UA |
| Konfigurasi dan sender lama ikut terbawa | Konfigurasi robot dan endpoint OPC UA saja |

Node ID, nama tag, security policy, namespace dan format JSON snapshot tetap kompatibel dengan paket OPC UA sebelumnya. Pemisahan C# reader dan server OPC UA di source tetap dipertahankan untuk kemudahan perawatan.

Tidak ada script pembaruan yang memasang database atau menulis row di SQL Server. Pengaturan database dilakukan melalui Ignition.
