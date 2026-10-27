# AppInstaller

Instalator aplikacji w stylu Ninite: **jeden plik `AppInstaller.exe` (~0,5 MB)**, który zawsze pobiera **najnowsze wersje** programów, instaluje je po cichu i ma fallback między źródłami. Do tego generator webowy na GitHub Pages (bez własnej domeny), który tworzy exe z gotowym wyborem aplikacji.

## Co jest w środku

| Wymaganie | Rozwiązanie |
|---|---|
| Zawsze najnowsze wersje | Adres instalatora ustalany w chwili instalacji: GitHub Releases API, oficjalne metadane .NET, stałe linki „latest” wydawców, skrypty PowerShell czytające stronę wydawcy, winget, Chocolatey |
| Jeden mały exe | WPF na .NET Framework 4.8 (wbudowany w Windows 10 1903+ / 11) — zero zależności, ~0,5 MB |
| Bezpieczna autoaktualizacja | `update.json` podpisany RSA-3072, klucz publiczny wbudowany w exe; tylko wyższa wersja, HTTPS, dozwolone hosty, SHA-256, zgodność podpisu Authenticode; atomowa podmiana exe |
| Windows 11 UI | Mica, akcent systemowy, ciemny/jasny motyw jak w systemie, Segoe UI Variable + Segoe Fluent Icons |
| Presety tylko z CLI | `--preset biuro`, `--preset-file`, `--apps`, `-q` |
| Pakiet naraz | Pobieranie równoległe (4), instalacja po kolei (Windows Installer nie pozwala na równoległe MSI) |
| Kilka źródeł z fallbackiem | Kolejność z katalogu, np. strona wydawcy → winget → Chocolatey; winget/choco wykrywane i instalowane dopiero gdy potrzebne |
| Ikona i opis | `icon` + `desc` (PL/EN) dla każdej aplikacji |
| Wariant webowy jak Ninite | `web/index.html` na GitHub Pages dokleja wybór do exe |
| Domyślne pakiety bez możliwości wyłączenia | `core` w katalogu: VC++ AIO (abbodi1406), .NET Desktop Runtime 8/9/10 x64+x86, Edge WebView2 Runtime, DirectX 9.0c |
| Łatwa modyfikacja | Cały katalog w `catalog/apps.json`; teksty UI w `Core/Loc.cs`; adres strony w `Core/Config.cs` |
| PL/EN, ciemny/jasny | Przełączniki w nagłówku (aplikacja i strona), domyślnie wg systemu |

## Pierwsze uruchomienie (raz)

1. Utwórz repozytorium na GitHubie, np. `AppInstaller`, i wrzuć ten katalog.
2. W `src/AppInstaller/Core/Config.cs` ustaw `BaseUrl = "https://TWOJ-LOGIN.github.io/AppInstaller/"`.
3. Wygeneruj klucze podpisu:
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\New-SigningKey.ps1
   ```
   Zacommituj `keys/public.xml`. Zawartość `keys/private.xml` dodaj jako sekret repozytorium **`SIGNING_KEY_XML`**, a sam plik trzymaj offline (jest w `.gitignore`).
4. **Settings → Pages → Source: GitHub Actions.**
5. Wydanie: `git tag v1.0.0 && git push --tags`. Workflow zbuduje exe, utworzy Release i opublikuje stronę:
   - `https://TWOJ-LOGIN.github.io/AppInstaller/` — generator
   - `apps.json(.sig)`, `update.json(.sig)`, `AppInstaller.exe`

Zmiana samego katalogu (push do `main`) publikuje nowy podpisany katalog bez nowego wydania — wszystkie exe w terenie pobiorą go przy następnym uruchomieniu.

## Budowanie lokalne

Wystarczy [.NET SDK](https://dotnet.microsoft.com/download) (Visual Studio niepotrzebne):

```cmd
build.cmd
```

Wynik: `out\AppInstaller.exe`. Bez `keys\public.xml` exe działa wyłącznie na katalogu wbudowanym (bez zdalnego katalogu i aktualizacji).

## Linia poleceń

```text
AppInstaller.exe                              okno wyboru
AppInstaller.exe --preset biuro               preset z oknem postępu
AppInstaller.exe --preset biuro,dev -q        bez okna; kod wyjścia 0 / 1 (błędy) / 3010 (restart)
AppInstaller.exe --apps firefox,7zip,vlc
AppInstaller.exe --preset-file moj.json       {"apps":["firefox","7zip"]}
AppInstaller.exe --source winget,direct       wymuszona kolejność/zakres źródeł
AppInstaller.exe --list | --list-presets
                 --lang pl|en  --log PLIK  --no-update  --offline
```

Pakiety bazowe są zawsze dołączane na początku listy. Logi: `%ProgramData%\AppInstaller\logs`.

## Generator webowy (jak Ninite)

Strona ładuje `apps.json` i `AppInstaller.exe` z tej samej domeny GitHub Pages, dokleja do exe wybór (`[JSON][długość]["AIPAYLD1"]`) i zapisuje go w przeglądarce — bez serwera. Taki exe po uruchomieniu od razu instaluje wybrane aplikacje.

Druga opcja, **„Wybór w nazwie pliku”**, nie zmienia ani bajtu exe (zachowuje podpis cyfrowy): `AppInstaller[firefox+7zip+vlc].exe`. Działa też ręczne przemianowanie pliku. Offline: `tools\New-Bundle.ps1`.

Payload zawiera wyłącznie identyfikatory aplikacji. Polecenia instalacji pochodzą zawsze z podpisanego katalogu, więc spreparowany exe nie może przemycić skryptu.

## Dodawanie aplikacji

Dopisz obiekt w `catalog/apps.json`:

```json
{
  "id": "nazwa", "name": "Nazwa", "category": "utilities", "icon": "@domena-wydawcy.com",
  "desc": { "pl": "Opis…", "en": "Description…" },
  "detect": "^Nazwa",
  "sources": [
    { "type": "github", "repo": "wlasciciel/repo", "asset": "^Setup-[\\d\\.]+-x64\\.exe$", "args": "/S" },
    { "type": "winget", "id": "Wydawca.Nazwa" },
    { "type": "choco",  "id": "nazwa" }
  ]
}
```

Typy źródeł:

| `type` | Pola | Uwagi |
|---|---|---|
| `github` | `repo`, `asset` (regex) | najnowsze wydanie; SHA-256 z GitHub weryfikowany automatycznie |
| `dotnet` | `channel`, `product`, `rid` | oficjalne metadane Microsoft, weryfikacja SHA-512 |
| `url` | `url`, `fileName` | stały link „zawsze najnowsza” od wydawcy |
| `ps` | `script` | PowerShell wypisuje adres instalatora (scraping strony wydawcy) |
| `winget` | `id`, `arch`, `extra` | winget instalowany automatycznie, jeśli go brak (również LTSC) |
| `choco` | `id`, `arch`, `extra` | Chocolatey instalowany automatycznie, jeśli go brak |

Dla `winget`: `"asUser": true` = instalacja dla zalogowanego użytkownika bez uprawnień administratora.

Wspólne dla źródeł bezpośrednich: `args`, `installer` (`exe`/`msi`/`extract`), `signer` (wymagany podpis Authenticode z tym wydawcą), `okCodes`.

- `"args": "auto"` — program rozpoznaje typ instalatora (Inno Setup, NSIS, WiX Burn, Squirrel, Advanced Installer, InstallShield) i sam dobiera cichy przełącznik; nieznany typ = przejście do następnego źródła, bez otwierania kreatora.
- `"installer": "extract"` — samorozpakowujące archiwum: najpierw plik z `extractArgs` (`{dir}` = folder docelowy), potem `run` z `args`. Tak działa DirectX offline: `/Q /C /T:"{dir}"` → `DXSETUP.exe /silent`.

`icon`: `@domena` (usługa ikon z `iconService`), pełny URL lub ścieżka względna do strony (np. `icons/app.png` z katalogu `catalog/icons`).

### Ikony

Kolejność prób: `catalog/icons/<id>.png` z repozytorium → adresy z pola `icons` (`@domena` = favicon przez `iconService`) → monogram.
Żeby ikony były pewne i szybkie, uruchom raz `tools\Get-Icons.ps1` — zapisze je do `catalog/icons`. Złą ikonę podmień własnym PNG o tej samej nazwie i zrób push.

### Instalacja jako zwykły użytkownik

Źródło winget z `"asUser": true` (Spotify, TIDAL) jest uruchamiane przez jednorazowe zadanie Harmonogramu na poziomie „Limited”, bo te instalatory odmawiają pracy z uprawnieniami administratora. Program instaluje się dla zalogowanego użytkownika.

## Bezpieczeństwo — w skrócie

- Katalog z zewnątrz jest używany **tylko** z ważnym podpisem (zawiera skrypty uruchamiane jako administrator). Inaczej: ostatnia zweryfikowana kopia lub katalog wbudowany.
- Pobrane instalatory: tylko HTTPS; sprawdzany skrót (GitHub/.NET) i podpis Authenticode (uszkodzony podpis = odrzucenie; `signer` = wymagany wydawca).
- Przejęcie GitHub Pages nie wystarczy do podmiany programu — potrzebny jest klucz prywatny.
- Exe nie jest podpisany certyfikatem Authenticode, więc SmartScreen pokaże ostrzeżenie „nieznany wydawca”. Darmowo dla open source: SignPath Foundation; płatnie: Azure Trusted Signing.

## Struktura

```text
catalog/apps.json          katalog aplikacji, kategorie, pakiety bazowe, presety
src/AppInstaller/Core/     silnik: źródła, fallback, winget/choco, podpisy, aktualizacje, CLI, payload
src/AppInstaller/Themes/   style Fluent (Light/Dark/Controls)
src/AppInstaller/*.xaml    okno główne
web/index.html             generator webowy
tools/                     klucze, podpisywanie i składanie strony, bundle offline
.github/workflows/         build + Release + GitHub Pages
```
