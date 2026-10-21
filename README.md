# Volumetric Explosions

The game's flat explosion sprites replaced by a small simulation of fire, smoke and dust, drawn as a true volume and
lit by the sun, the sky and the fire. It changes nothing but the look.

![A tank of fuel bursts beside the launch pad: the fireball lifts on its own heat into dark smoke, burning pieces fly clear trailing smoke, and fire burns on where the fuel spilt](src/VolumetricExplosions/img/volumetric-explosions.gif)

![The game's own explosion and this mod's](src/VolumetricExplosions/img/explosions-compare.jpg)

*Left: the game's own explosion. Right: the same blast with the mod.*

![A fuel tank's fireball beside the launch pad at night, lighting the pad as it rolls up into dark smoke](src/VolumetricExplosions/img/volumetric-explosions-fireball.gif)

![The fires a fuel explosion leaves burning on the ground, their smoke climbing](src/VolumetricExplosions/img/volumetric-explosions-fire.gif)

For Kerbal Space Program 1.12.x. It needs no other mod.

## What you get

* **What blew up decides the look:** fuel gives an orange fireball and thick dark smoke, solid propellant a white
  flash and pale smoke, a battery a blue-white flash. So does where: thin air, a vacuum, night.
* **Smoke that behaves like smoke:** it rises and rolls, drifts on the wind, lies on the ground, goes round buildings
  and is pushed about by craft.
* **Shock fronts, pieces and burn marks,** shadows under the smoke, and fires that burn on where fuel was spilt; a big
  blast's front lifting a ring of dust off bare ground, pieces throwing up dust or spray where they land, burn marks
  as real ones look, lopsided and streaked with soot, no two alike (0.8.0).
* **Its own bang** (0.8.0): a crack close by, a boom and a long rumble far off, as big as what went off, heard when its
  sound gets to you.
* **Other mods' resources** count as the stock ones they are like, for what each is worth beside it (liquid hydrogen as
  fuel with a clear flame, liquid oxygen as oxidiser, argon as xenon, and so on): `PluginData/resources.cfg` lists all of
  the Community Resource Pack's. With RealBattery, a battery's charge counts for all it holds. (By Rjoande.)
* **Engines' flames and smoke,** with [Engine Flames](https://github.com/IshiakiZ/ksp-engine-flames) and
  [Rocket Smoke](https://github.com/IshiakiZ/ksp-rocket-smoke), which are made in the same air.

## Install

Copy the `VolumetricExplosions` folder from the download into your KSP's `GameData`. To remove it, delete that folder.

On a Mac, a copy downloaded with a browser runs slower until you allow its library with one command: see
[Installing](https://github.com/IshiakiZ/ksp-volumetric-explosions/wiki/Installing).

## Settings

In `GameData/VolumetricExplosions/PluginData/settings.cfg`: `quality` (0.2 light to 3 heavy), `size`, `smoke` (how
long it hangs), `wind`, and `enabled = false` for the game's own explosions.
[All of them](https://github.com/IshiakiZ/ksp-volumetric-explosions/wiki/Settings).

## Good to know

* What it costs: a 4-tonne fuel fire filling half the view took the game from 79 frames a second to 56 at first and
  70 to 75 once it was smoke (an Apple M5 Pro), and from 165 to 141 on an NVIDIA RTX 4080 SUPER, both at 1280 x 720.

## Works on

Mac: tried. Windows 11 (Direct3D 11 and OpenGL): this version tried (launches, crashes, the fire's mended light). Linux: never run.

**More:** [the long description](https://github.com/IshiakiZ/ksp-volumetric-explosions/wiki/The-long-description) (everything in full,
and what was tried on each system) and the rest of the [wiki](https://github.com/IshiakiZ/ksp-volumetric-explosions/wiki).

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.
