# Menjalankan Collector OPC UA di Windows

Gunakan .NET 10 x64. Buka root project dan jalankan `scripts\publish-opcua.cmd`.

Hasil build berada di `publish\opcua`:

- `TEST_OPCUA.cmd`: server dengan data simulasi.
- `START_OPCUA.cmd`: server dengan data aktual YMConnect.
- `CHECK_SDK.cmd`: memeriksa DLL YMConnect tanpa menghubungkan robot.
- `TEST_MOCK.cmd`: alias pengujian simulasi OPC UA.

Pilih satu server pada endpoint yang sama. Tekan Ctrl+C untuk berhenti. Pengaturan ada pada `config\collector.opcua.conf` dan `config\robots.opcua.conf`.

Program baru bernama `rms_collector_opcua.exe`. Konfigurasi Gateway dan trust certificate dijelaskan lengkap pada `PANDUAN_IGNITION_OPCUA.md`.

Untuk memperbarui paket sebelumnya, baca `MIGRATION.md` sebelum menyalin file.
