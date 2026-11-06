# Graph Report - DataRecoveryApp  (2026-11-06)

## Corpus Check
- 39 files · ~21,782 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 5 file(s) not represented in the graph (top: .donotdelete 1, (none) 1, .manifest 1)

## Summary
- 664 nodes · 1228 edges · 22 communities (17 shown, 5 thin omitted)
- Extraction: 96% EXTRACTED · 4% INFERRED · 0% AMBIGUOUS · INFERRED: 51 edges (avg confidence: 0.86)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `5620817b`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- Window
- SafeFileHandle
- RecoverableFile
- RustBridge
- DataRecoveryApp.Helpers
- CultureInfo
- lib.rs
- MainViewModel
- RecoverableFile
- scanner.rs
- CarverSettings
- List
- partition.rs
- MainWindow.xaml.cs
- 🔍 Veri Kurtarma — v0.3.1 (Faz 3 + Rust Çekirdeği)
- DiskImageProgress
- RelayCommand
- MainViewModel.AddResults
- Task
- DataRecoveryApp.csproj
- recovery_engine

## God Nodes (most connected - your core abstractions)
1. `Window` - 70 edges
2. `MainViewModel` - 66 edges
3. `RecoverableFile` - 34 edges
4. `CarverSettings` - 23 edges
5. `RustBridge` - 22 edges
6. `RawReader` - 22 edges
7. `WinApiHelper` - 21 edges
8. `PartitionInfo` - 20 edges
9. `PartitionAnalysisResult` - 19 edges
10. `DataRecoveryApp.Helpers` - 18 edges

## Surprising Connections (you probably didn't know these)
- `Bölüm JSON'u` --references--> `PartitionService`  [INFERRED]
  docs/RUST_CORE_PLAN.md → Services/PartitionService.cs
- `Windows'ta DLL konumu` --references--> `RustBridge`  [INFERRED]
  docs/RUST_CORE_PLAN.md → Services/RustBridge.cs
- `MainWindow` --calls--> `MainViewModel`  [INFERRED]
  MainWindow.xaml.cs → ViewModels/MainViewModel.cs
- `MainViewModel` --calls--> `RelayCommand`  [INFERRED]
  ViewModels/MainViewModel.cs → Helpers/RelayCommand.cs
- `MainViewModel` --references--> `BulkObservableCollection`  [EXTRACTED]
  ViewModels/MainViewModel.cs → Helpers/BulkObservableCollection.cs

## Import Cycles
- None detected.

## Communities (22 total, 5 thin omitted)

### Community 0 - "Window"
Cohesion: 0.03
Nodes (68): AnalyzePartitionsCommand, AppVersion, BrowseDestinationCommand, CancelScanCommand, CarverSettings.BMP, CarverSettings.DOCX, CarverSettings.EXE, CarverSettings.GIF (+60 more)

### Community 1 - "SafeFileHandle"
Cohesion: 0.06
Nodes (32): BY_HANDLE_FILE_INFORMATION, NTFS_VOLUME_DATA_BUFFER, READ_USN_JOURNAL_DATA_V0, USN_JOURNAL_DATA_V0, WinApiHelper, PartitionAnalysisResult, BytesPerSector, DiskNumber (+24 more)

### Community 2 - "RecoverableFile"
Cohesion: 0.10
Nodes (9): FileTypeHelper, PreviewData, Content, Metadata, Title, Type, FileDataReader, PreviewService (+1 more)

### Community 3 - "RustBridge"
Cohesion: 0.05
Nodes (30): Bilinen sınırlamalar, Bölüm JSON'u, Derleme (çapraz derleme, Linux/WSL), Dışa aktarılan C fonksiyonları, Hata kodları, İmza kimlikleri (`sig_id`), Mimari, Rust Çekirdeği (recovery_engine) — Uygulama Belgesi (+22 more)

### Community 4 - "DataRecoveryApp.Helpers"
Cohesion: 0.09
Nodes (10): DataRecoveryApp.Helpers, DataRecoveryApp.ViewModels, DataRecoveryApp.Services, DataRecoveryApp.Models, ObservableObject, DriveViewModel, DisplayText, Drive (+2 more)

### Community 5 - "CultureInfo"
Cohesion: 0.16
Nodes (5): BitmapToImageSourceConverter, FileCategoryIconConverter, PreviewTypeToVisibilityConverter, RecoverabilityColorConverter, DataRecoveryApp.Converters

### Community 6 - "lib.rs"
Cohesion: 0.08
Nodes (30): cb(), cstr(), ffi_scan_parse_read(), guard(), HITS, LOCK, partitions_json(), RE_ERR_ACCESS_DENIED (+22 more)

### Community 7 - "MainViewModel"
Cohesion: 0.05
Nodes (34): MainViewModel, AnalyzePartitionsCommand, AppVersion, BrowseDestinationCommand, CancelScanCommand, CanScan, CarverSettings, Categories (+26 more)

### Community 8 - "RecoverableFile"
Cohesion: 0.06
Nodes (28): FileCategory, Audio, Document, Image, Other, Video, RecoverableFile, DataOffset (+20 more)

### Community 9 - "scanner.rs"
Cohesion: 0.07
Nodes (44): BLOCK_SIZE, CANCEL, CHUNK, ESTIMATED_FLAG, find(), find_footer(), finds_all_signatures_with_sizes(), Hit (+36 more)

### Community 10 - "CarverSettings"
Cohesion: 0.10
Nodes (20): CarverSettings, AnyZipFamily, BMP, DOCX, EXE, GIF, JPEG, MP4 (+12 more)

### Community 11 - "List"
Cohesion: 0.07
Nodes (22): DriveInfoModel, DisplayName, FileSystem, FreeSpace, Label, Letter, TotalSize, RecoveryProgress (+14 more)

### Community 12 - "partition.rs"
Cohesion: 0.09
Nodes (24): open(), detect_fs(), gpt_type_name(), gpt_with_efi_partition(), guid_string(), mbr_type_name(), mbr_with_ntfs_and_fat32(), no_signature_is_error() (+16 more)

### Community 13 - "MainWindow.xaml.cs"
Cohesion: 0.15
Nodes (6): Application, bd, App, DataRecoveryApp, MainWindow, Border

### Community 14 - "🔍 Veri Kurtarma — v0.3.1 (Faz 3 + Rust Çekirdeği)"
Cohesion: 0.17
Nodes (11): 1. Gereksinimler, 2. Kaynaktan çalıştırma, 3. Tek dosyalı .exe oluşturma, 4. Yönetici izni, Bilinen sınırlamalar, Kullanım ipuçları, Proje yapısı, Rust çekirdeği (recovery_engine) (+3 more)

### Community 15 - "DiskImageProgress"
Cohesion: 0.17
Nodes (11): DiskImageProgress, BadBytes, BytesRead, Percent, TotalBytes, PreviewType, Image, Metadata (+3 more)

### Community 21 - "DataRecoveryApp.csproj"
Cohesion: 0.40
Nodes (4): net8.0-windows, System.Drawing.Common (10.0.14), System.Management (8.0.0), Microsoft.NET.Sdk

## Knowledge Gaps
- **222 isolated node(s):** `MainWindow`, `AppVersion`, `EngineStatus`, `RefreshDrivesCommand`, `StartScanCommand` (+217 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 335 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **5 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `MainViewModel` connect `MainViewModel` to `Window`, `SafeFileHandle`, `RecoverableFile`, `DataRecoveryApp.Helpers`, `RecoverableFile`, `CarverSettings`, `List`, `MainWindow.xaml.cs`, `RelayCommand`, `MainViewModel.AddResults`, `Task`?**
  _High betweenness centrality (0.259) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `MainViewModel` (e.g. with `MainWindow` and `RelayCommand`) actually correct?**
  _`MainViewModel` has 2 INFERRED edges - model-reasoned connections that need verification._
- **What connects `MainWindow`, `AppVersion`, `EngineStatus` to the rest of the system?**
  _222 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Window` be split into smaller, more focused modules?**
  _Cohesion score 0.028985507246376812 - nodes in this community are weakly interconnected._
- **Why does `Window` connect `Window` to `MainWindow.xaml.cs`, `MainViewModel`?**
  _High betweenness centrality (0.148) - this node is a cross-community bridge._
- **Should `SafeFileHandle` be split into smaller, more focused modules?**
  _Cohesion score 0.06034801925212884 - nodes in this community are weakly interconnected._
- **Why does `DataRecoveryApp.Models` connect `DataRecoveryApp.Helpers` to `List`, `CultureInfo`, `DiskImageProgress`?**
  _High betweenness centrality (0.062) - this node is a cross-community bridge._