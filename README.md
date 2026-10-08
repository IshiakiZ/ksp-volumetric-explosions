# Volumetric Explosions

A Kerbal Space Program 1 mod that replaces the game's flat explosion sprites with a small simulation of
fire, smoke and dust, drawn as a true volume.

Thousands of particles are carried by a model of the air, gathered into a 3D grid of smoke and heat, lit
by the sun, the sky and the fire, and drawn by a shader that walks a ray through that grid for every pixel:
the way Counter-Strike 2 draws its smoke grenades. There are no pictures of explosions in this mod. It
needs no other mod and changes nothing but the look.

![The game's own explosion and this mod's](src/VolumetricExplosions/img/explosions-compare.jpg)

*Left: the game's own explosion. Right: the same blast with the mod.*

![One fuel fire from the flash to the smoke, and the mark a crash leaves](src/VolumetricExplosions/img/explosions-sequence.jpg)

## Install

Copy `GameData/VolumetricExplosions` into your KSP `GameData` folder and start the game. To remove it,
delete that folder. For KSP 1.12.x; it needs no other mod.

On a Mac, a copy downloaded with a browser runs slower until you allow its library with one command:
see [Installing](docs/Installing.md).

## What you get

* **What blew up decides the look.** Fuel and oxidiser give an orange fireball and thick dark smoke; solid
  propellant a white-hot flash and pale smoke; monopropellant a pale yellow fire; a battery a blue-white
  flash; xenon a cold white burst; ore, dust. The size comes from how much there was.
* **So do why and where.** A crash throws fire and dust on ahead; thin air lets a blast spread wide; in a
  vacuum there is no smoke; without oxygen, fuel alone does not burn; at night smoke is lit by its own fire.
* **Smoke that behaves like smoke.** It rises and rolls into a mushroom, drifts on a wind (when there is
  one), lies on the ground as the ground really is and runs downhill when it has cooled, goes round
  buildings and ships, and is pushed about by craft and their exhaust.
* **Shock fronts** that bend the picture as they pass, **pieces** of the destroyed parts, and **burn marks**
  thrown onto whatever surface is there.

![Each kind by itself: fuel and oxidiser (fireball, then smoke), fuel alone, solid propellant (flash, then smoke), monopropellant, a battery bank, a xenon tank, and fuel alone on Duna](src/VolumetricExplosions/img/explosions-kinds.jpg)

*Each panel is a blast of its own. The battery and the xenon tank are seen from closer: they are small.*

## Settings

In `GameData/VolumetricExplosions/settings.cfg`. The ones most worth knowing: `quality` (0.2 light to 3
heavy), `size`, `smoke` (how long it hangs), `wind`, and `enabled = false` to have the stock explosions
back. [All of them](docs/Settings.md).

## Where it works

| System | |
| --- | --- |
| Mac | Made and tested here (KSP 1.12.5, OpenGL). |
| Linux | The game runs on OpenGL there too, so it should work the same. **Never run.** |
| Windows | On the game's default Direct3D the smoke is drawn as soft sprites, not as a volume, until the shaders are built for it. Starting the game with `-force-glcore` should give the full picture. **Never run.** |

What it costs: on an Apple M5 Pro at 1280 x 720, a 4-tonne fuel fire filling half the view took the game
from 79 frames a second to 56 in its first seconds and 70 to 75 once it was smoke.

## More

* [What decides how an explosion looks](docs/What-decides-how-an-explosion-looks.md)
* [What the smoke does](docs/What-the-smoke-does.md)
* [Shock fronts, debris and burn marks](docs/Shock-fronts-debris-and-burn-marks.md)
* [How it is drawn, and where](docs/How-it-is-drawn.md)
* [What it costs](docs/What-it-costs.md) and
  [what was tested](docs/What-was-tested.md)
* [Limits](docs/Limits.md)
* [Building it yourself](docs/Building.md)

`./build.sh` builds the mod and installs it; it needs the .NET SDK and a copy of the game.

## Licence

[MIT](LICENSE).
