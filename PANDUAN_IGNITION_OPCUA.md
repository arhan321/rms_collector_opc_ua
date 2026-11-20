# RMS Collector OPC UA Only → Ignition → Database

Revisi OPC UA Only v2, 20 November 2026. Basis pembaca robot: `arhan321/rms-collector-net`, commit `63e9b1a5384d173b9cbee818fe4e8e9808e362ad`.

## 1. Yang berubah

Alur sistem: **controller Yaskawa → Data Collector C# / YMConnect → OPC UA → Ignition → Database**.

Satu program utama bernama `rms_collector_opcua.exe`. Konfigurasi pengiriman API, token, HTTP client dan spool telah dihapus dari source aplikasi. Semua data keluar melalui tag pada endpoint `opc.tcp`. Pengaturan memakai file `.conf`.

C# tetap diperlukan sebagai penghubung ke YMConnect. Ini tidak memasang server OPC UA pada controller robot. Ignition terhubung ke **PC Collector**, bukan ke IP robot melalui OPC UA.

Pembaca YMConnect yang sudah ada tetap dipakai: pulse dan torque enam axis, status controller/servo, job Master, operating counters, alarm aktif, snapshot alarm history, I/O byte, dan identitas controller. Kode pembaca tetap hanya membaca; tidak menambahkan perintah gerak, reset alarm, penulisan I/O, atau start job.

**Penyimpanan ke database menjadi tanggung jawab Ignition.** Collector menyediakan data, kualitas dan timestamp; Collector tidak membuka koneksi database. Koneksi SQL Server dan proses pencatatan perlu diaktifkan pada Gateway Ignition sesuai langkah 7. Kode ini tidak otomatis mengonfigurasi Gateway atau database pengguna.

## 2. Paket dan penempatan file

- `RMS_OPCUA_ONLY_CHANGED_FILES_V2.zip`: ekstrak ke **root project Collector** yang sudah ada. Pertahankan struktur folder dalam ZIP. Berisi isi lengkap file yang berubah dari paket sebelumnya serta daftar file yang dihapus. Jalankan `BERSIHKAN_FILE_LAMA.cmd` setelah menyalin perubahan agar source lama tidak tertinggal. Baca `MIGRATION.md` lebih dahulu dan simpan konfigurasi IP/endpoint milik sendiri.
- `RMS_OPCUA_ONLY_FULL_SOURCE_V2.zip`: source Collector lengkap khusus OPC UA, SDK YMConnect, panduan, dan tes. Proyek utama hanya satu: `RmsCollector.csproj`.
- `RMS_OPCUA_ONLY_WINDOWS_X64_V2.zip`: hasil publish program OPC UA untuk Windows x64. Ekstrak ke folder tersendiri, misalnya `C:\RMS-Collector-OPCUA-Only`. Paket ini memerlukan **.NET Runtime 10 x64**; PC dengan .NET SDK 10 x64 juga dapat menjalankannya.

Ekstrak paket executable ke folder Collector tersendiri. File utama source:

| Path dari root Collector | Fungsi |
| --- | --- |
| `RmsCollector.csproj` | Satu-satunya proyek executable Collector, berbasis OPC UA |
| `src-csharp/Program.cs` | Startup OPC UA, pembacaan robot, reconnect, publish |
| `src-csharp/Configuration.cs` | Pengaturan robot dan polling, tanpa pengaturan pengiriman API |
| `src-csharp/Models.cs` | Model sampel dan serialisasi JSON untuk tag OPC UA |
| `opcua/OpcUaHost.cs` | Server OPC UA dan certificate trust |
| `opcua/RmsNodeManager.cs` | Struktur tag, status kualitas, dan deteksi stale |
| `opcua/OpcUaSettings.cs` | Validasi konfigurasi OPC UA |
| `config/collector.opcua.conf` | Endpoint, path konfigurasi robot dan certificate store |
| `config/robots.opcua.conf` | IP robot, alamat I/O, dan interval baca |
| `scripts/publish-opcua.cmd` | Build/publish Windows x64 |
| `deploy/windows/START_OPCUA.cmd` | Launcher pembacaan robot untuk folder publish |
| `deploy/windows/TEST_OPCUA.cmd` | Launcher simulasi untuk folder publish |
| `tests-opcua/` | Tes koneksi OPC UA nyata melalui loopback dengan data simulasi |

Proyek utama mengompilasi pembaca di `src-csharp` dan server di `opcua`. `Transport.cs` dan proyek executable lama sudah dihapus. Pertahankan folder `src-csharp`, `opcua`, dan `sdk/dotnet`. Node ID dan struktur JSON snapshot tetap kompatibel dengan paket OPC UA sebelumnya.

## 3. Atur koneksi sebelum menjalankan

Buka `config/collector.opcua.conf`:

```ini
robot_config=config/robots.opcua.conf
endpoint=opc.tcp://localhost:4845/RMS/Collector
pki_dir=./pki-opcua
```

Jika Gateway Ignition berada di PC yang sama dengan Collector, gunakan `localhost`. Jika Gateway berada di PC lain, ganti `localhost` dengan **IP atau hostname PC Collector yang dapat dijangkau Gateway**, misalnya:

```ini
endpoint=opc.tcp://192.168.10.20:4845/RMS/Collector
```

IP tersebut contoh, bukan IP robot. Pilih alamat final sebelum pertama kali menjalankan program agar certificate dibuat dengan alamat yang benar. TCP port 4845 dipilih untuk menghindari port OPC UA bawaan Ignition; jangan gunakan port yang sedang dipakai aplikasi lain. Jika beda PC, izinkan TCP 4845 pada firewall PC Collector untuk PC Gateway yang digunakan.

Path relatif dihitung dari **folder kerja saat program dijalankan**. Launcher di paket Windows otomatis berpindah ke foldernya sendiri.

Buka `config/robots.opcua.conf` dan sesuaikan IP/alamat I/O dengan konfigurasi controller yang memang dipakai:

```ini
telemetry_ms=5000
operating_every_s=60
alarms_every_s=30
extras_every_s=10
job_monitor_every_sample=1

robot=1,192.168.255.1,R1,10;10010
```

Format robot: `ID,IP_CONTROLLER,GROUP,ALAMAT_IO_BYTE`. `10;10010` adalah daftar alamat **byte I/O** untuk dibaca, bukan jumlah robot atau pengaturan job. Gunakan alamat yang sudah dikonfirmasi untuk controller; jangan menebak alamat dari label pendant. Jika tidak membutuhkan I/O:

```ini
robot=1,192.168.255.1,R1
```

Untuk dua controller, tambahkan baris robot dengan ID dan IP unik:

```ini
robot=1,192.168.255.1,R1,10;10010
robot=2,192.168.255.2,R1,10;10010
```

Pembatasan pembaca asli masih berlaku: **1–8 controller dengan group R1**. R2, station group, dan beberapa group dalam satu controller belum didukung oleh pembaca ini. Semua tag dipisahkan berdasarkan ID robot. Tidak perlu membuat satu server OPC UA untuk setiap robot.

`job_monitor_every_sample=1` membaca job setiap siklus telemetry; `0` membacanya pada interval extras. File contoh menetapkan nilainya langsung. Lebih sering membaca OPC di Ignition tidak membuat pembacaan robot lebih cepat daripada `telemetry_ms`.

## 4. Jalankan

Untuk paket Windows hasil publish:

1. Jalankan `TEST_OPCUA.cmd` untuk menguji sambungan Ignition menggunakan simulasi. Tidak memerlukan robot. Data ditandai `Simulated=true` dan kualitas `UncertainSubstituteValue`.
2. Lakukan pertukaran trust certificate pada langkah 5.
3. Setelah pengujian, hentikan simulasi dengan Ctrl+C. Jalankan `START_OPCUA.cmd` untuk pembacaan robot aktual. Jangan jalankan kedua program pada endpoint yang sama secara bersamaan.

Untuk build dari source, dari root project Collector:

```bat
scripts\publish-opcua.cmd
```

Hasilnya di `publish\opcua`. Jalankan launcher yang berada **di folder hasil publish**, bukan langsung di `deploy\windows`.

Alternatif menjalankan source dengan SDK .NET 10:

```bat
dotnet run --project RmsCollector.csproj -- --config config\collector.opcua.conf --mock
dotnet run --project RmsCollector.csproj -- --config config\collector.opcua.conf --real
```

Pilih satu perintah sesuai kebutuhan. Pada mode aktual, YMConnect tetap memerlukan Windows x64, DLL vendor yang berpasangan, serta komunikasi PC ke controller yang sudah benar pada proyek sebelumnya. Masalah komunikasi controller tidak dapat diselesaikan hanya dengan membuat koneksi OPC UA ke Ignition.

## 5. Hubungkan Ignition dan percayai certificate kedua arah

Collector menyediakan `Basic256Sha256` dengan **SignAndEncrypt**. User identity memakai Anonymous, tetapi certificate aplikasi client tetap harus dipercaya oleh server. Tidak ada opsi untuk mengabaikan certificate atau menerima semua client otomatis.

1. Jalankan Collector. Pada startup pertama dibuat folder `pki-opcua` di PC tersebut. Pertahankan folder ini saat update aplikasi agar identitas certificate tidak berubah.
2. Pada Gateway Ignition 8.3 buka **Connections → OPC → Connections → Create OPC Connection**, pilih **OPC UA Connection**. Pada Ignition 8.1 menu konfigurasi lama berada di **Config → OPC Client → OPC Connections**.
3. Masukkan endpoint Collector sesuai konfigurasi, misalnya `opc.tcp://localhost:4845/RMS/Collector` jika Gateway dan Collector satu PC. Ini koneksi ke external OPC UA server, bukan pembuatan device driver Yaskawa pada halaman Devices.
4. Pilih endpoint **Basic256Sha256 / SignAndEncrypt**, percaya certificate server Collector setelah identitasnya diperiksa, beri nama koneksi misalnya `RMS_YMConnect`. Jika tersedia pilihan Authentication Type, pilih **Anonymous**. Pada versi lama, biarkan username/password kosong. Pilih Read Only jika tersedia.
5. Percobaan koneksi pertama dapat gagal karena Collector belum mempercayai certificate **client Ignition**. Periksa log `[OPCUA CERT]` dan certificate baru dalam `pki-opcua\rejected\certs`.
6. Cocokkan certificate itu dengan certificate **OPC UA Client** Gateway Ignition pada halaman OPC UA Security/Certificates. Bandingkan identitas dan thumbprint yang sama; jangan memasukkan certificate aplikasi lain. Sesudah cocok, salin file publik `.der` milik client tersebut dari `rejected\certs` ke `pki-opcua\trusted\certs`. Buat folder tujuan jika belum ada. Jangan menyalin private key.
7. Restart Collector agar trust store dibaca ulang. Reconnect koneksi di Ignition. Status yang diharapkan: **Connected**.
8. Buka OPC Browser di Designer, pilih koneksi `RMS_YMConnect`, lalu browse **Objects → RMS → Robots → 1**. Buat OPC tags dari hasil browse. Untuk robot lain pilih ID masing-masing. Pilih Tag Group sekitar 1000–5000 ms untuk memulai.

Sertifikat publik server berada di `pki-opcua\own\certs` jika perlu dipercaya lewat pengaturan Ignition secara manual. Jangan membagikan folder `own\private`. Paket download tidak menyertakan certificate atau private key dari komputer pengujian.

Dokumentasi resmi Ignition:

- https://docs.inductiveautomation.com/docs/8.3/ignition-modules/opc-ua/opc-ua-connections
- https://docs.inductiveautomation.com/docs/8.3/ignition-modules/opc-ua/opc-ua-security
- https://www.docs.inductiveautomation.com/docs/8.1/ignition-modules/opc-ua/opc-ua-client-connection-settings

## 6. Tag utama

Namespace URI: `urn:adm:rms:ymconnect:opcua`. String identifier setiap robot berawal `RMS/Robots/{ID}/`. Contoh identifier torque S: `RMS/Robots/1/Telemetry/Torque/S`. Namespace index numerik dapat berbeda antarserver; gunakan hasil browse Ignition, jangan mengasumsikan `ns=2`.

| Tag relatif pada robot | Tipe | Keterangan |
| --- | --- | --- |
| `RobotId`, `IpAddress`, `Group` | Int32 / String | Identitas konfigurasi |
| `Simulated` | Boolean | Selalu periksa sebelum memakai data produksi |
| `Connected` | Boolean | Koneksi SDK terakhir; tidak menjamin semua jenis pembacaan berhasil |
| `HasFreshTelemetry` | Boolean | Minimal pulse atau torque terbaru valid |
| `ConnectionMessage` | String | Diagnostik koneksi |
| `Telemetry/Torque/S` sampai `/T` | Double | Enam axis S,L,U,R,B,T; unit `sdk_raw` |
| `Telemetry/Pulse/S` sampai `/T` | Double | Posisi dalam pulses |
| `Telemetry/Torque/ReadOk`, `StatusCode` | Boolean / Int32 | Hasil baca torque SDK |
| `Telemetry/Pulse/ReadOk`, `StatusCode` | Boolean / Int32 | Hasil baca posisi SDK |
| `Telemetry/SessionId`, `Sequence` | String / Int64 | Identitas proses robot dan urutan sampel |
| `Telemetry/SnapshotJson` | String JSON | Satu snapshot berisi axes, identitas, kualitas SDK, status, job evidence dan metadata asli |
| `Status/Running`, `ServoOn`, `Alarming`, `Hold`, `Error` | Boolean | Status controller |
| `Status/ControlMode`, `CycleMode` | String | Mode controller |
| `Job/Name`, `Line`, `Step` | String / Int32 | Executing job task Master |
| `Job/SpeedOverridePercent` | Double | Speed override; bukan kecepatan axis |
| `Job/ReadAtUtc`, `ReadStatus` | DateTime / String | Waktu dan hasil pembacaan job |
| `Job/OemDiagnosisAvailable` | Boolean | `false`: pembaca belum menyediakan OEM job diagnosis |
| `Operating/{Counter}/Seconds`, `Hours` | Int64 / Double | Counter kumulatif robot |
| `Operating/{Counter}/ReadOk` | Boolean | Ketersediaan setiap counter |
| `Alarms/ActiveCount`, `ActiveJson` | Int32 / JSON | Jumlah dan daftar alarm aktif |
| `Alarms/HistoryJson`, `HistoryReadOk` | JSON / Boolean | Snapshot history per kategori dari pembaca asli |
| `IO/Byte10`, `IO/Byte10010` | Int32 | Nilai byte 0–255; tag tergantung alamat di konfigurasi |
| `IO/Json` | String JSON | Hasil tiap alamat berikut status SDK |
| `System/Json` | String JSON | Informasi controller dan status baca tiap group |

Nama `{Counter}`: `ControllerOnTime`, `ServoPowerOnTimeRobot`, `PlayBackTimeRobot`, `MovingTimeRobot`. Nilai tersebut bukan durasi per job.

Setiap stream `Telemetry`, `Status`, `Job`, `Operating`, `Alarms`, `IO`, `System` juga memiliki `ReadOk` dan `SampledAtUtc`. `System/ReadOk` aktual mengacu keberhasilan informasi group robot R1; status station/application tambahan tersedia dalam JSON.

Semua tag bersifat **read-only**, tanpa historical-read OPC UA. Nilai waktu memakai UTC; format tampilan WIB dapat diatur di Ignition. Source timestamp merupakan waktu host Collector, bukan timestamp presisi internal controller.

## 7. Ignition → Database, kualitas dan histori

Untuk menyelesaikan alur sampai SQL Server, pada Gateway buat koneksi database ke server yang digunakan. Sesudah koneksi valid, pilih mekanisme pencatatan sesuai kebutuhan:

- **SQL Historian / Tag History** untuk histori tag numerik, kualitas dan waktu. Pada Ignition 8.3, provider SQL Historian membutuhkan modul Historian Core dan SQL Historian. Tabel histori dikelola Ignition; jangan mengasumsikan strukturnya sama dengan tabel aplikasi yang sudah ada.
- **Historical Transaction Group (SQL Bridge)** untuk pencatatan OPC/tag ke kolom tabel yang dipilih. Pilih tipe Historical dan aliran pembacaan ke database; tag Collector bersifat read-only. Ikuti kemampuan modul yang tersedia pada Gateway.
- Untuk data job dan sampel yang perlu tetap satu identitas, gunakan `Telemetry/SnapshotJson` sebagai acuan satu record bersama `collector_session_id` dan `sequence`. Pemetaan atau parser penyimpanan khusus masih perlu dibuat di Ignition jika ingin memecah JSON menjadi beberapa kolom. Jangan menggabungkan tag job dan torque yang dibaca terpisah seolah-olah samplingnya atomik.

Tag muncul di OPC Browser **belum berarti data sudah disimpan di database**. Aktifkan logging, lalu cek row baru, kualitas dan waktu di SQL Server. Koneksi dan penulisan database belum dapat diuji terhadap Gateway pengguna dari paket ini.

Sumber resmi untuk pengaturan penyimpanan:

- https://docs.inductiveautomation.com/docs/8.3/platform/database-connections
- https://docs.inductiveautomation.com/docs/8.3/ignition-modules/tag-historian/tag-history-providers
- https://docs.inductiveautomation.com/docs/8.3/ignition-modules/sql-bridge-transaction-groups


- Belum dibaca: `BadWaitingForInitialData`, bukan nol.
- Baca aktual berhasil: `Good`.
- Simulasi berhasil: `UncertainSubstituteValue`, bukan data aktual. Ignition dapat menampilkan indikator kualitas Uncertain; ini memang penanda simulasi.
- Baca gagal atau koneksi hilang: `BadNoCommunication`. Nilai tidak boleh ditafsirkan normal hanya karena angka lama masih terlihat.
- Data basi: kualitas menjadi Bad dan source timestamp lama dipertahankan. TTL per stream = dua kali intervalnya + 5 detik. Default telemetry/status/job 15 detik, extras 25 detik, alarm 65 detik, operating 125 detik.

`Connected=true` tidak berarti torque, job dan semua I/O pasti valid. Gunakan kualitas OPC dan `ReadOk` untuk jenis data yang dipakai. Pada kegagalan baca terbaru, field yang gagal berisi null/Bad. Pada stale/disconnect, nilai terakhir dapat tetap terlihat dengan kualitas Bad.

Collector membaca data pada interval konfigurasi dan Ignition melakukan subscription terhadap nilai terbaru. Collector ini **tidak memiliki antrean persisten untuk merekam semua sampel saat Ignition offline**. Sampel saat Ignition offline tidak otomatis diputar ulang. Monitored item OPC juga bukan jaminan tersimpannya setiap sampel polling. Untuk histori, atur penyimpanan Ignition/SQL dan uji kebutuhan retensi, deadband serta rate. Snapshot alarm history adalah hasil polling yang dapat berulang; sistem penyimpan berikutnya harus mencegah duplikasi kejadian alarm.

`Telemetry/SnapshotJson` membantu konsumen yang membutuhkan satu objek berisi axes dan identitas. Pembacaan job, torque dan status pada controller tetap pemanggilan SDK terpisah; snapshot ini bukan jaminan sampling hardware serentak atau batas awal/akhir satu siklus job.

Grafik torque/pulse bisa dibuat di Ignition dari tag dan history yang direkam. **Speed Reducer Life OEM, ampere aktual, dan position error tidak otomatis menjadi tersedia hanya karena transport berpindah ke OPC UA**. Torque tetap `sdk_raw`, tidak diklaim sebagai N·m atau ampere. Rumus dan sumber diagnosis OEM tidak ditambahkan dalam paket ini.

## 8. Verifikasi dan troubleshooting

Tes yang disediakan (jalankan dari root source dengan SDK .NET 10):

```bat
dotnet run --project tests-opcua\RmsCollector.OpcUa.Tests.csproj
```

Tes OPC membuka server dan client OPC UA lokal dengan certificate nyata, menguji browse dua robot, subscription, pemetaan nilai, kegagalan baca, stale/recovery dan penolakan write. Data sumber tes tetap simulasi. Tes ini tidak menggantikan uji Windows, Ignition terpasang, atau controller DOJO.

| Gejala | Periksa |
| --- | --- |
| `BadCertificateUntrusted` / security check failed | Trust public certificate di kedua aplikasi, jangan hanya di Ignition; restart sesudah mengubah trust folder |
| IP/hostname tidak cocok | Endpoint yang diumumkan harus alamat PC Collector yang bisa diakses Gateway dan cocok certificate |
| Port already in use | Hentikan instance Collector lain pada port sama atau ubah port |
| Runtime tidak ditemukan | Install .NET Runtime 10 x64 atau .NET SDK 10 x64 |
| DLL YMConnect gagal dimuat | Windows x64, pasangan DLL resmi dan dependency native yang dibutuhkan SDK |
| Ignition Connected tetapi tag Bad | Periksa IP robot, jaringan SDK dan hasil `ReadOk`/`StatusCode`; koneksi OPC berbeda dengan koneksi robot |
| Grafik Ignition tidak punya history | Aktifkan penyimpanan history yang dibutuhkan; tag OPC hanya memberikan nilai terbaru |

Uji di PC DOJO: pastikan `Simulated=false`, bandingkan nilai/status dengan pendant, putuskan dan pulihkan koneksi saat kondisi uji yang disepakati, lalu cocokkan perubahan quality/timestamp di Ignition. Paket ini belum dijalankan terhadap hardware robot atau Gateway Ignition milik pengguna.
