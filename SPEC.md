# PowerLink — SPEC.md

> Ten plik jest jedynym źródłem kontekstu dla agenta budującego projekt.
> Zawiera pełną specyfikację, decyzje projektowe, wiedzę techniczną i plan implementacji.

---

## 1. Wizja projektu

PowerLink to narzędzie do **deduplikacji plików przez hardlinki NTFS** oraz **klonowania katalogów bez duplikowania danych**.

Docelowo ma trafić jako moduł do [Microsoft PowerToys](https://github.com/microsoft/PowerToys) (130k★, C#/C++ + WinUI 3, MIT License). Na początek budujemy **standalone aplikację** w tym samym stacku co PowerToys, żeby migracja była trywialna.

### Motywacja

Użytkownik ma katalog `models/` z wieloma podkatalogami (modele ML: `.bin`, `.safetensors`, `.gguf`). Wiele plików jest identycznych, ale leżą w różnych lokalizacjach. Cele:

1. **Deduplikacja** — przeskanować 2+ katalogi, znaleźć identyczne pliki (po hash), zastąpić duplikaty hardlinkami → odzyskać miejsce na dysku.
2. **Clone** — skopiować katalog do nowej lokalizacji, ale zamiast kopiować pliki — tworzyć hardlinki → zero dodatkowego miejsca.

### Inspiracja

Projekt inspirowany [Link Shell Extension (LSE)](https://schinagl.priv.at/nt/hardlinkshellext/linkshellextension.html) — dojrzałe narzędzie (od 1999 r., v3.9.3.5 z 2022) autorstwa Hermanna Schinagla, które dodaje do Windows Explorera pełne GUI do linków NTFS. LSE obsługuje hardlinki, junctions, symlinki, volume mountpoints, smart copy/clone/mirror/DeLorean copy. PowerLink celowo zaczyna od wąskiego scope (deduplikacja + clone) i rozwija się iteracyjnie.

### Na GitHubie PowerToys istnieje wieloletnie zapotrzebowanie

Od 2020 roku pojawiają się regularne feature requesty:
- [#2527](https://github.com/microsoft/PowerToys/issues/2527) — "PowerSymlink - Creates Symlinks from Context Menu"
- [#10047](https://github.com/microsoft/PowerToys/issues/10047) — "GUI wrapper for MKLink"
- [#17887](https://github.com/microsoft/PowerToys/issues/17887) — "Symbolic link creation function"
- [#24571](https://github.com/microsoft/PowerToys/issues/24571) — "Softlink / Hardlink"
- [#26607](https://github.com/microsoft/PowerToys/issues/26607) — "mklink, symlinks" (wprost proponuje odtworzenie LSE w PowerToys)
- [#34247](https://github.com/microsoft/PowerToys/issues/34247) — "Support 3 link type in Windows context menu"

Większość zamykana jako duplikat — feature nie jest jeszcze na roadmapie PowerToys.

---

## 2. Wiedza techniczna o hardlinkach NTFS

### Czym jest hardlink

W NTFS plik to dwie rzeczy:
- **Dane** (bloki na dysku, obiekt w MFT)
- **Wpis katalogowy** (nazwa wskazująca na dane)

Zwykły plik: jeden wpis → jedne dane.
Hardlink: drugi wpis → **te same dane**.

NTFS nie rozróżnia "oryginału" i "kopii" — oba wpisy są równorzędne. NTFS trzyma `reference count` (licznik referencji) w MFT.

### Miejsce na dysku

Plik 100 MB + hardlink = nadal 100 MB na dysku. Nowy wpis katalogowy to kilkadziesiąt bajtów w MFT. Reference count rośnie z 1 do 2.

### Usunięcie jednego z linków

Usunięcie pliku = usunięcie wpisu katalogowego. Reference count maleje. Dane znikają z dysku **dopiero gdy ref count spadnie do 0** (ostatni link usunięty). Usunięcie `a.txt` nie wpływa na `b.txt` jeśli oba są hardlinkami do tych samych danych.

### Edycja

- **Edycja in-place** (program otwiera plik, zmienia bajty, zapisuje z powrotem) — zmiana widoczna przez wszystkie hardlinki natychmiast. Bo to fizycznie te same bloki.
- **Edycja save-as-new** (program tworzy plik tymczasowy, kasuje stary, przenosi nowy) — **link się rwie**. Po takiej operacji każdy link wskazuje na inne dane. Wiele edytorów tak robi (VS Code, Notepad++, Word).

Dla modeli ML to nie problem — pliki są read-only po pobraniu. Nikt ich nie edytuje.

### Ograniczenia hardlinków

- Działają **wyłącznie na plikach**, NIE na katalogach (NTFS nie wspiera hardlinków katalogów — mogłyby tworzyć cykle)
- Oba linki muszą być na **tym samym woluminie NTFS**
- Limit **1023 hardlinków na plik** (wystarczający)
- Plik musi nie być w użyciu (locked) w momencie tworzenia linku
- Nie działają na FAT/exFAT/ReFS<3.5

### API do hardlinków

```csharp
// P/Invoke
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

// Sprawdzenie ref count
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
// BY_HANDLE_FILE_INFORMATION.nNumberOfLinks = reference count
```

### Procedura zamiany duplikatu na hardlink

Dwa kroki (NIE trzy — nie trzeba pliku tymczasowego):
```
1. DeleteFile(duplicate_path)         // usuwa wpis katalogowy duplikatu
2. CreateHardLink(duplicate_path, canonical_path)  // tworzy hardlink w miejscu duplikatu
```

Po `DeleteFile` ścieżka jest wolna. `CreateHardLink` ją zajmuje. Hash potwierdził identyczność, więc nawet gdyby krok 2 się nie powiódł, dane nie są zagrożone (canonical nadal istnieje).

### UAC / uprawnienia

**Hardlinki NIE wymagają elevacji** — zwykły użytkownik może je tworzyć w swoich folderach. To największa przewaga nad symlinkami (które wymagają `SeCreateSymbolicLinkPrivilege` lub Developer Mode).

**Junctions** (odpowiednik hardlinków dla katalogów) też nie wymagają elevacji w normalnych lokalizacjach. Wymagają tylko w chronionych folderach (`C:\Program Files` itp.).

**Symlinki** wymagają elevacji — scope na przyszłość (Faza 2+).

---

## 3. Scope MVP (Faza 1 — standalone aplikacja)

### Funkcja A: Deduplikacja

1. Użytkownik podaje 2+ ścieżki katalogów
2. Rekurencyjny scan: buduje mapę plików
3. Grupowanie po rozmiarze (szybki filtr — pliki o różnym rozmiarze nie mogą być identyczne)
4. Hashowanie tylko w obrębie grup o tym samym rozmiarze
5. Dla dużych plików: hashowanie blokami z wczesnym odrzuceniem (jeśli pierwszy blok się różni, nie czytaj reszty)
6. Raport: ile duplikatów, ile miejsca do odzyskania, lista grup
7. Dry-run domyślnie (pokaż co by się zmieniło, nie zmieniaj)
8. Po potwierdzeniu: zamiana duplikatów na hardlinki

### Funkcja B: Clone katalogów

1. Użytkownik wskazuje katalog źródłowy i ścieżkę docelową
2. Rekurencyjne tworzenie struktury katalogów w celu
3. Dla każdego pliku: `CreateHardLink()` zamiast kopiowania
4. Wynik: identyczna struktura, zero dodatkowego miejsca

### Czego NIE robimy w Fazie 1

- Shell extension (menu kontekstowe Explorera)
- Overlay icons
- Property sheet tab
- Symlinki / Junctions
- Smart Copy / Smart Move / DeLorean Copy
- GUI (Faza 1 to CLI lub minimalne WinUI 3)

---

## 4. Architektura i struktura projektu

```
PowerLink/
│
├── PowerLink.sln                      # Solution file
│
├── src/
│   ├── PowerLink.Core/                # Silnik — ZERO zależności od UI
│   │   ├── PowerLink.Core.csproj      # .NET 8, library
│   │   ├── Models/
│   │   │   ├── FileRecord.cs          # path, size, hash, nLinks
│   │   │   ├── DuplicateGroup.cs      # lista FileRecord z tym samym hashem
│   │   │   └── ScanResult.cs          # wynik skanowania: grupy, statystyki
│   │   ├── Scanning/
│   │   │   ├── FileScanner.cs         # rekurencyjny scan katalogów
│   │   │   └── HashCalculator.cs      # SHA-256/xxHash, blokowe hashowanie
│   │   ├── Dedup/
│   │   │   ├── DedupEngine.cs         # grupowanie + plan deduplikacji
│   │   │   └── DedupExecutor.cs       # wykonanie planu (delete + hardlink)
│   │   ├── Clone/
│   │   │   └── CloneEngine.cs         # klonowanie katalogu przez hardlinki
│   │   └── Native/
│   │       └── Win32Hardlink.cs       # P/Invoke: CreateHardLink, GetFileInformationByHandle
│   │
│   ├── PowerLink.Cli/                 # CLI — prosty interfejs na start
│   │   ├── PowerLink.Cli.csproj       # .NET 8, console app
│   │   └── Program.cs                 # komendy: scan, dedup, clone
│   │
│   └── PowerLink.App/                 # WinUI 3 — standalone GUI (opcjonalne w MVP)
│       ├── PowerLink.App.csproj       # Windows App SDK
│       ├── MainWindow.xaml
│       └── Pages/
│           ├── ScanPage.xaml          # wybór katalogów + wynik skanowania
│           └── ClonePage.xaml         # klonowanie
│
├── tests/
│   └── PowerLink.Core.Tests/          # xUnit testy silnika
│       ├── PowerLink.Core.Tests.csproj
│       ├── FileScannerTests.cs
│       ├── HashCalculatorTests.cs
│       ├── DedupEngineTests.cs
│       └── CloneEngineTests.cs
│
├── SPEC.md                            # ten plik
├── README.md
├── LICENSE                            # MIT (jak PowerToys)
└── .gitignore
```

### Zasady architektoniczne

1. **PowerLink.Core nie ma żadnych zależności od UI** — to czysta logika. Referencja tylko do .NET BCL. To pozwoli potem wrzucić ją do PowerToys `src/modules/powerlink/` bez zmian.
2. **Wszystkie operacje Win32 w jednym miejscu** — `Win32Hardlink.cs` opakowuje P/Invoke. Reszta kodu operuje na abstrakcjach.
3. **Async/await + IProgress<T>** — scan i deduplikacja mogą trwać minuty na dużych katalogach. UI musi dostawać progress. Silnik raportuje postęp przez `IProgress<ScanProgress>`.
4. **Dry-run jako domyślny tryb** — `DedupEngine.CreatePlan()` zwraca plan (co zostanie zmienione), `DedupExecutor.Execute(plan)` realizuje go. Nigdy nie zmieniaj plików bez jawnego potwierdzenia.
5. **CancellationToken wszędzie** — użytkownik musi móc przerwać scan/dedup w dowolnym momencie.

---

## 5. Kluczowe klasy — kontrakty

### FileRecord

```csharp
public record FileRecord
{
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public string? Hash { get; set; }           // null dopóki nie policzony
    public uint HardLinkCount { get; init; }     // z GetFileInformationByHandle
    public ulong FileIndex { get; init; }        // unikalny identyfikator pliku w NTFS (z BY_HANDLE_FILE_INFORMATION)
}
```

### DuplicateGroup

```csharp
public record DuplicateGroup
{
    public required string Hash { get; init; }
    public required long FileSize { get; init; }
    public required List<FileRecord> Files { get; init; }

    public FileRecord Canonical => Files[0];                    // ten zostaje
    public IEnumerable<FileRecord> Duplicates => Files.Skip(1); // te zamieniamy na hardlinki
    public long WastedBytes => FileSize * (Files.Count - 1);    // ile miejsca do odzyskania
}
```

### ScanResult

```csharp
public record ScanResult
{
    public required List<DuplicateGroup> Groups { get; init; }
    public long TotalFilesScanned { get; init; }
    public long TotalDuplicates => Groups.Sum(g => g.Duplicates.Count());
    public long TotalWastedBytes => Groups.Sum(g => g.WastedBytes);
    public TimeSpan ScanDuration { get; init; }
}
```

### DedupPlan

```csharp
public record DedupAction
{
    public required string DuplicatePath { get; init; }   // plik do usunięcia
    public required string CanonicalPath { get; init; }   // plik do którego tworzymy hardlink
    public required long SizeBytes { get; init; }
}

public record DedupPlan
{
    public required List<DedupAction> Actions { get; init; }
    public long TotalBytesToRecover => Actions.Sum(a => a.SizeBytes);
}
```

---

## 6. Algorytm deduplikacji — szczegóły

```
SCAN:
  for each input directory (recursive):
    for each file:
      stat = GetFileInformationByHandle(file)
      record = { path, size, nLinks, fileIndex }
      // WAŻNE: jeśli dwa pliki mają ten sam fileIndex,
      // to są już hardlinkami do siebie — pomijamy
      add to records[]

GROUP BY SIZE:
  sizeGroups = records.GroupBy(r => r.SizeBytes)
  // Odrzuć grupy z 1 plikiem — nie mogą mieć duplikatów
  candidates = sizeGroups.Where(g => g.Count() > 1)

HASH:
  for each size group:
    for each file in group:
      // Dwuetapowe hashowanie dla dużych plików:
      // 1. Hash pierwszych 4KB → szybki filtr
      // 2. Jeśli prefix-hash się zgadza → hash pełnego pliku
      file.Hash = ComputeHash(file.FullPath)

GROUP BY HASH:
  hashGroups = all_files.GroupBy(r => r.Hash)
  duplicateGroups = hashGroups.Where(g => g.Count() > 1)

PLAN:
  for each duplicateGroup:
    canonical = group.Files[0]  // dowolny, ale preferuj ten z najniższym fileIndex
    for each other in group.Files[1..]:
      plan.Add(Delete(other) → CreateHardLink(other.path, canonical.path))

EXECUTE (po potwierdzeniu użytkownika):
  for each action in plan:
    DeleteFile(action.DuplicatePath)
    CreateHardLink(action.DuplicatePath, action.CanonicalPath)
    // W razie błędu: loguj, kontynuuj z następnym plikiem
```

### Wybór algorytmu hashowania

- **xxHash (XXH3/XXH128)** — preferowany. ~30 GB/s na nowoczesnym CPU, 128-bit wystarczający do deduplikacji. NuGet: `System.IO.Hashing` (wbudowany w .NET 8, klasa `XxHash128`).
- **SHA-256** — fallback jeśli potrzebna kryptograficzna pewność. ~500 MB/s. W .NET: `System.Security.Cryptography.SHA256`.
- Dla plików >100 MB: hashuj blokami 64KB, przerwij wcześnie jeśli prefix się różni.

---

## 7. Algorytm klonowania

```
CLONE(sourcePath, destPath):
  if sourcePath and destPath are on different volumes:
    ERROR — hardlinki nie działają cross-volume

  for each directory in sourcePath (recursive, depth-first):
    mirrorDir = destPath + relativePath(directory, sourcePath)
    CreateDirectory(mirrorDir)

  for each file in sourcePath (recursive):
    mirrorFile = destPath + relativePath(file, sourcePath)
    CreateHardLink(mirrorFile, file)
    // W razie błędu: loguj, kontynuuj
```

---

## 8. CLI interface (MVP)

```
powerlink scan <path1> <path2> [<pathN>...]
    Skanuje katalogi, pokazuje raport duplikatów.
    --min-size <bytes>    Minimalna wielkość pliku (domyślnie 1MB — pomijaj małe pliki)
    --output <json|table> Format raportu (domyślnie table)

powerlink dedup <path1> <path2> [<pathN>...]
    Skanuje + deduplikuje.
    --dry-run             Domyślnie włączony. Pokaż plan, nie wykonuj.
    --execute             Wyłącz dry-run, wykonaj deduplikację.
    --min-size <bytes>    j.w.

powerlink clone <source> <dest>
    Klonuje katalog przez hardlinki.
    --dry-run             Pokaż co by zostało sklonowane.
```

---

## 9. Przyszłe fazy (NIE implementuj w MVP)

### Faza 2 — Shell Extension + Overlay Icons
- Menu kontekstowe Explorera: "Clone as hardlinks", "Pick link source" / "Drop as hardlink"
- IExplorerCommand (Win11 nowe menu kontekstowe)
- IShellIconOverlayIdentifier — ikony na hardlinkach w Explorerze
- UWAGA: Windows ma limit ~11-15 slotów overlay icons (OneDrive, Dropbox, Git itp. zajmują większość)

### Faza 3 — Symlinki + Junctions
- Symlinki: wymagają UAC lub Developer Mode (SeCreateSymbolicLinkPrivilege)
- Junctions: odpowiednik hardlinków dla katalogów, nie wymagają UAC
- Smart Move: aktualizacja junctions/symlinków przy rename/move

### Faza 4 — Zaawansowane operacje
- Smart Copy: kopiowanie z zachowaniem wewnętrznej struktury linków
- DeLorean Copy: inkrementalny backup przez hardlinki (clone + mirror)
- Smart Mirror: synchronizacja z zachowaniem hardlinków
- Backup Mode: kopiowanie ACL, alternative streams, szyfrowane pliki

### Faza 5 — Integracja z PowerToys
- Migracja PowerLink.Core do src/modules/powerlink/
- Strona ustawień w PowerToys Settings (WinUI 3)
- Integracja z Command Palette: "create hardlink", "clone folder", "deduplicate"
- Template modułu: DLL ładowany przez PowerToys runner

---

## 10. Wymagania techniczne

- **.NET 8+** (LTS)
- **Windows 10 1809+** / Windows 11
- **NTFS** (jedyny obsługiwany system plików w MVP)
- **Brak zewnętrznych zależności w Core** (tylko System.IO.Hashing z BCL)
- **xUnit** dla testów
- **Licencja MIT** (zgodnie z PowerToys)

---

## 11. Instrukcje dla agenta

### Kolejność budowania

1. `PowerLink.Core` — zacznij od `Win32Hardlink.cs` (P/Invoke wrapper), potem `HashCalculator.cs`, `FileScanner.cs`, `DedupEngine.cs`, `CloneEngine.cs`
2. `PowerLink.Core.Tests` — testy dla każdej klasy, testy integracyjne tworzące prawdziwe hardlinki na dysku
3. `PowerLink.Cli` — prosty Program.cs z System.CommandLine lub ręcznym parsowaniem args

### Styl kodu

- Nullable reference types: enable
- File-scoped namespaces
- Primary constructors gdzie sensowne
- Preferuj `record` nad `class` dla DTO/modeli
- Async/await + CancellationToken na każdej operacji I/O
- IProgress<T> dla raportowania postępu
- Logowanie: Microsoft.Extensions.Logging (ILogger<T>)

### Testowanie

- Testy tworzą pliki tymczasowe w `Path.GetTempPath()/PowerLinkTests/`
- Po testach: cleanup (usuwanie plików/katalogów)
- Testuj scenariusze brzegowe:
  - Plik zablokowany przez inny proces
  - Pliki na różnych woluminach (powinien zwrócić błąd)
  - Plik z ref count > 1 (już jest hardlinkiem)
  - Puste katalogi
  - Bardzo długie ścieżki (>260 znaków — używaj prefixu `\\?\`)
  - Brak uprawnień do katalogu

---

## 12. Diagram architektury (ASCII)

```
┌─────────────────────────────────────────────────┐
│                PowerLink.Cli                     │
│         (lub PowerLink.App — WinUI 3)            │
└──────────────────────┬──────────────────────────┘
                       │ calls
                       ▼
┌─────────────────────────────────────────────────┐
│              PowerLink.Core                      │
│                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────────┐  │
│  │FileScanner│  │DedupEngine│  │ CloneEngine  │  │
│  └─────┬────┘  └─────┬────┘  └──────┬───────┘  │
│        │             │               │           │
│        ▼             ▼               ▼           │
│  ┌──────────┐  ┌──────────┐  ┌──────────────┐  │
│  │HashCalc   │  │DedupExec │  │Win32Hardlink │  │
│  └──────────┘  └─────┬────┘  └──────┬───────┘  │
│                      │               │           │
│                      └───────┬───────┘           │
│                              ▼                   │
│                    ┌──────────────┐               │
│                    │ P/Invoke:     │               │
│                    │ CreateHardLink│               │
│                    │ DeleteFile    │               │
│                    │ GetFileInfo   │               │
│                    └──────────────┘               │
└─────────────────────────────────────────────────┘
                       │
                       ▼
              ┌────────────────┐
              │  NTFS Volume   │
              └────────────────┘
```
