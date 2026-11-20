# RMS Collector — OPC UA Only

Satu Collector C#/.NET 10 untuk membaca controller Yaskawa melalui YMConnect dan menyediakan data read-only ke Ignition.

**Alur: Controller Yaskawa → Data Collector → OPC UA → Ignition → Database.**

## Mulai

1. Atur IP/controller dan interval di `config/robots.opcua.conf`.
2. Atur alamat PC Collector di `config/collector.opcua.conf`.
3. Build dengan `scripts\publish-opcua.cmd`.
4. Dari `publish\opcua`, jalankan `TEST_OPCUA.cmd` untuk simulasi, atau `START_OPCUA.cmd` untuk robot aktual.
5. Buat koneksi client OPC UA di Ignition dan percayai certificate kedua arah.
6. Aktifkan koneksi database dan logging di Ignition.

Panduan lengkap: [PANDUAN_IGNITION_OPCUA.md](PANDUAN_IGNITION_OPCUA.md). Jika memperbarui paket sebelumnya, baca [MIGRATION.md](MIGRATION.md).

## Build dan tes dari root

```bat
dotnet build RmsCollector.csproj -c Release
dotnet run --project RmsCollector.csproj -- --mock
dotnet run --project tests-opcua\RmsCollector.OpcUa.Tests.csproj
```

### jika mau running production
Cek SDK .NET 10:
```
dotnet --list-sdks
```
Build aplikasinya:
```
dotnet build .\RmsCollector.csproj -c Release
```
Jalankan pembacaan robot aktual:
```
.\bin\Release\net10.0\rms_collector_opcua.exe --config .\config\collector.opcua.conf --real
```


Proyek executable hanya `RmsCollector.csproj`. Kode startup berada di `src-csharp/Program.cs`; komponen server di `opcua/`. Binary bernama `rms_collector_opcua.exe`.

Konfigurasi hanya melalui `.conf`, tanpa pengaturan environment. Pembacaan aktual membutuhkan Windows x64 dan SDK YMConnect berpasangan yang sudah disertakan pada `sdk/dotnet`.

## Data dan batas

Tag meliputi torque/pulse S,L,U,R,B,T, status servo/controller, job task Master, operating counters, alarm, I/O byte dan identitas controller. Node IDs dan JSON snapshot tetap sama dengan paket OPC UA sebelumnya.

Keamanan koneksi: Basic256Sha256, SignAndEncrypt, explicit certificate trust. Semua tag read-only. Simulasi ditandai `Simulated=true` dan kualitas Uncertain; pembacaan gagal atau basi ditandai Bad.

Ignition bertanggung jawab menyimpan data ke database. Database belum otomatis dikonfigurasi oleh source Collector ini. Tidak ada historical-read atau antrean persisten sampel pada Collector; uji pengaturan history/retensi di Ignition sebelum menggunakan data untuk laporan.

Pembaca saat ini mendukung 1–8 controller, group R1. Torque berunit `sdk_raw`; tidak menambahkan diagnosis OEM, konversi ampere aktual atau position error.

