# VS Cleaner

Aplikasi WPF simple untuk menghapus hasil debug Visual Studio agar build berikutnya lancar.

## Cara pakai
1. Jalankan `VsCleaner.exe` (tutup Visual Studio dulu)
2. Browse folder root berisi .sln (misal `Documents\Visual Studio 2022\Projects`)
3. Centang target: `bin`, `obj`, `.vs`, `TestResults` (default aman). Biarkan `Buang ke Recycle Bin` dicentang agar bisa restore.
4. Klik Scan → centang hasil → Hapus yang Dipilih
5. Klik baris → Buka di Explorer (atau double-click / klik kanan) untuk memeriksa sebelum hapus.

## Scan Orphan (eksperimental)
Klik `Scan Orphan…` untuk cek keterhubungan semua file vs `.sln/.csproj/.vcxproj` + token `#include`:
- `sampah`: `*.tmp *.log *.bak *.ilk` + `Thumbs.db` — relatif aman.
- `tak terpakai?`: biner/arsip besar (>2MB) yang namanya tak disebut di file proyek.
- `orphan?`: source C++ klasik tak terdaftar di `.vcxproj`, atau source di luar folder proyek mana pun.
Hasil orphan TIDAK dicentang otomatis — wajib review manual. SDK-style C# (glob implisit) tidak diflag sebagai orphan.

## Yang dihapus
- `bin/` output build
- `obj/` intermediate
- `.vs/` cache VS
- `TestResults/`
- Opsional: `Debug/ Release/ x64/ x86/` (hanya jika ada .sln/.csproj/.vcxproj di folder induk)
- Opsional: `*.user *.suo *.sdf` + `project.lock.json`
- Source code (`.sln .csproj .cpp .cs`) TIDAK PERNAH dihapus

## Build
```powershell
dotnet build VsCleaner/VsCleaner.csproj -c Release
```

## Publish single EXE ringan (butuh .NET 10 runtime, 193KB)
```powershell
dotnet publish VsCleaner/VsCleaner.csproj -c Release -o VsCleaner/publish-fd /p:PublishSingleFile=true /p:SelfContained=false
```

## Publish single EXE mandiri (tanpa install .NET, ~140MB)
```powershell
dotnet publish VsCleaner/VsCleaner.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o VsCleaner/publish
```
