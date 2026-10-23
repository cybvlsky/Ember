# Ember

Sterownik ekranu **Turing Smart Screen 3.5"** (TURZX, rewizja A, USB `1a86:5722`, numer seryjny `USB35INCHIPSV2`).
Działa w tle z zasobnika, startuje z Windows i pokazuje temperatury oraz obciążenie podzespołów w animowanych motywach.

## Instalacja

Kliknij dwa razy `Zainstaluj.cmd`. Skrypt zbuduje program, skopiuje go do `C:\Program Files\Ember`, doda skrót w menu Start i go uruchomi.
Przy pierwszym uruchomieniu z uprawnieniami administratora Ember zakłada zadanie w Harmonogramie zadań: od tej pory startuje po zalogowaniu, z dostępem do czujników i bez pytania UAC.

Odinstalowanie: `powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall`.

Temperatura procesora (Ryzen) wymaga sterownika **PawnIO**: podpisanego następcy WinRing0 używanego przez LibreHardwareMonitor. Ember pobierze i uruchomi oficjalny instalator (z weryfikacją podpisu) po kliknięciu „Zainstaluj PawnIO” na stronie Ekran.

## Dlaczego jest płynny

Ekran przyjmuje ok. **157 KB/s** (zmierzone na tym egzemplarzu, niezależnie od baud i kontroli przepływu), więc pełna klatka 480×320 trwa ok. 1,9 s. Ember:

- renderuje klatki w pamięci (SkiaSharp, ~1 ms) i wysyła tylko zmienione piksele, w małych prostokątach z priorytetem: drobne animacje idą od razu, duże zmiany dochodzą w kilku klatkach i nikt nie czeka w nieskończoność,
- ma cyfry o stałej szerokości, więc zmiana „41” na „42” to jeden znak, nie cały napis,
- animuje wskaźniki z dokładnością do pół piksela, a wykresy w trybie oscyloskopu zmieniają tylko wąski pas wokół pióra,
- stosuje dithering uporządkowany (RGB565 bez pasów na gradientach, a nieruchome piksele są identyczne między klatkami).

`Ember.exe --render <folder>` zapisuje podgląd każdego motywu, mapę cieplną zużycia łącza i raport pasma.
`Ember.exe --selftest` sprawdza protokół, konwersję kolorów, wysyłanie różnic na wirtualnym ekranie i wszystkie motywy.

## Stabilność

- Port szeregowy przez surowe WinAPI (bez ukrytego wątku `SerialPort`), limit zapisu 2 s. Odłączenie, uśpienie, zawieszony firmware: rozłączenie, ponowne wyszukanie portu co 2 s, w razie potrzeby restart ekranu.
- Czujniki przez LibreHardwareMonitor z PawnIO. Wyłączone są grupy, które dotykają magistrali SMBus (pamięć SPD, kontrolery); RAM i sieć czytane wprost z Windows. Czujniki płyty głównej są opcjonalne.
- Gdy Windows gasi monitory albo usypia komputer, ekranik też gaśnie. Przy wyłączaniu komputera dostaje komendę wyłączenia podświetlenia.
- Log: `%APPDATA%\Ember\ember.log` (rotacja po 1 MB).

## Motywy

Wbudowane: Ember, Aurora, Latte, Mono, Neon, Rdzenie, Terminal, Zegar. Własne są w `%APPDATA%\Ember\themes\*.json` (edytor zapisuje je tam sam).

Motyw to paleta i lista elementów rysowanych od dołu do góry:

```json
{
  "name": "Mój", "font": "Bahnschrift", "bg": "#090C11",
  "palette": { "a1": "#FFC53D", "a2": "#FF6A3D", "text": "#ECEFF4", "track": "#1B222E" },
  "widgets": [
    { "type": "gauge", "x": 20, "y": 44, "w": 116, "h": 116, "sensor": "cpu.load", "colors": ["a1", "a2"], "glow": 10, "mode": "dot" },
    { "type": "text", "x": 78, "y": 90, "align": "center", "text": "{cpu.temp:0}°", "size": 28, "sensor": "cpu.temp", "warn": 80, "crit": 90 }
  ]
}
```

Kolory: `#RGB`, `#RRGGBB`, `#RRGGBBAA` albo nazwa z palety, z opcjonalnym kryciem `a1/30`. Progi `warn`/`crit` płynnie przechodzą w kolory `warn` i `crit` palety.

| Typ | Do czego | Najważniejsze pola |
|---|---|---|
| `text` | napisy i wartości | `text` z `{klucz:format}`, `font`, `size`, `weight`, `stretch`, `align`, `spacing`, `glow`, `mode`: `upper`, `blink` |
| `gauge` | łuk / pierścień | `sensor`, `min`, `max`, `thickness`, `start`, `sweep`, `segments`, `gap`, `mode`: `dot`, `flat`, `ticks`, `alternate` |
| `bar` | pasek | `sensor`, `min`, `max`, `radius`, `segments`, `gap`, `mode`: `vertical` |
| `graph` | historia | `sensor`, `points`, `max` (brak = auto), `mode`: `sweep`, `dot`, `grid`, `sharp`, `nofill` |
| `cores` | słupek na każdy wątek | `sensor` = prefiks (`cpu.t`), `gap`, `radius` |
| `rect`, `glow`, `icon`, `image` | tło, karty, poświaty, ikony Segoe Fluent, obrazy | `color`/`colors`, `angle`, `radius`, `stroke`, `shadow`, `glyph`, `src` |

Formaty w tekście: zwykłe formaty liczb .NET (`0`, `0.0`), `rate` (B/s → KB/s / MB/s), `ratev` i `rateu` (sama liczba / sama jednostka), `mbit`, `ghz`, a dla `{time}` i `{date}` formaty daty (`HH:mm`, `dddd, d MMMM`). Pełna lista kluczy jest na stronie Czujniki (kliknięcie kopiuje klucz).
