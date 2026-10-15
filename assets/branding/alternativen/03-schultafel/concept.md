# Kreidezeit – Alternative 03 „Schultafel“

Das Icon zeigt eine klassische Schiefertafel im Holzrahmen, auf die von Hand ein Koordinatensystem mit einer gelben Parabel und der Beschriftung „f(x)“ gezeichnet ist – genau das, was ein Mathe-Lehrer in seinen Lehrvideos tut. Auf der unteren Rahmenleiste liegt ein Stück gelbe Kreide, leichte Wischspuren auf der Tafel erzählen von vorherigen Stunden („Kreidezeit“ als Zeitalter der Tafel). Die Kreidetextur entsteht rein geometrisch: unregelmäßig verwackelte Konturen, ausgestanzte Längsrillen und Poren sowie gestreuter Kreidestaub – keine SVG-Filter. Der Schriftzug ist eine eigens gezeichnete, leicht geneigte Lehrer-Druckschrift, die als Einlinien-Skelett konstruiert und mit derselben Kreidetextur versehen wurde; eine schwungvolle Unterstreichung bindet ihn an die gelbe Kurve im Icon.

## Farben

| Rolle | Hex |
|---|---|
| Schiefer (Verlauf oben) | `#34423F` |
| Schiefer (Verlauf unten) | `#1F2A28` |
| Holz hell / mittel / dunkel | `#C88A52` / `#A5683A` / `#6B4024` |
| Kreide weiß | `#F4F1E8` |
| Kreide gelb | `#F5CB52` |
| Ocker (Unterstreichung, heller Hintergrund) | `#C98F12` |
| Schriftzug auf Hell | `#22302D` |

## Schrift

Keine Systemschrift: Der Schriftzug „Kreidezeit“ und die Beschriftung „f(x)“ sind handgezeichnete Einlinien-Buchstaben (eigene Skelettpfade), per Python/Shapely zu Kreide-Konturen aufgebaut und als Pfade exportiert. Generator: `scratchpad/03/gen.py` (+ `chalk.py`, `hand.py`).
