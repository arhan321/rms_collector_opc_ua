# Struktur Collector OPC UA Only

| Komponen | Tugas |
| --- | --- |
| `src-csharp/Configuration.cs` | Membaca konfigurasi robot, I/O dan interval polling |
| `src-csharp/YmConnectReader.cs` | Membaca data controller secara read-only memakai SDK Yaskawa |
| `src-csharp/Readers.cs` | Kontrak reader dan sumber simulasi |
| `src-csharp/Models.cs` | Model sampel dan serialisasi JSON untuk tag snapshot |
| `src-csharp/JobMonitorEvidence.cs` | Metadata pembacaan job task Master |
| `src-csharp/Program.cs` | Startup aplikasi, polling, reconnect dan shutdown |
| `opcua/OpcUaHost.cs` | Hosting server OPC UA dan certificate store |
| `opcua/OpcUaSettings.cs` | Validasi endpoint dan path certificate |
| `opcua/RmsNodeManager.cs` | Folder/tag robot, source timestamp, kualitas dan stale detection |
| `tests-opcua/` | Tes integrasi client-server OPC UA lokal dan konfigurasi |

Satu sumber sampel diterbitkan sebagai tag terpisah dan `Telemetry/SnapshotJson`. Ignition berlangganan tag tersebut dan menjalankan proses penyimpanan database. Kode Collector tidak mengatur koneksi SQL Server.

Pembacaan job dan torque tetap merupakan pemanggilan SDK yang berbeda. Status RUN tidak sama dengan jumlah eksekusi job. Diagnosis umur reducer OEM dan arus motor aktual tidak dibuat dari nilai torque raw.
