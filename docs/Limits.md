# Limits

* On Windows' default Direct3D the smoke is sprites, not a volume, and burn marks are flat (see [How it is drawn](How-it-is-drawn.md)).
* The grid has 64 cells each way, stretched over the cloud, so its cells grow with the cloud. How smooth
  the smoke is comes from the particles and the billows from the shader, so the smoke does not coarsen
  with it; but a small fire at the foot of a very large cloud is drawn coarsely.
* From inside a large cloud its grid shows. With the camera in the smoke of a whole rocket, a cell of the
  grid is two metres across or more. Looked into on 2026-10-07: what shows is thick smoke ending, or light
  failing, across the width of one cell; the grid's cells lie east, north and up, so seen from inside along
  one of those lines such an edge is a straight line down the picture. Smoother reading of the grid would
  only soften it; a finer grid round the camera is what it wants, and has not been made.
* In next to no air (tried at 2.4% of Kerbin's sea level, 130 km up on Jool; presumably the same above
  about 25 km on Kerbin) an old knot of thick smoke shows as a block with rounded corners. The smoke
  there stays where the fire was instead of spreading, far thicker than the grid's cells can give an edge
  to, and with so little air there are next to no billows to cut the edge as they do lower down.
* Billows do not last: they are replaced as soon as the air has pulled them about one and a half times
  out of shape, which is half a second in a blast and half a minute in still air. How fast the air is
  doing that is taken from a grid of 32 cells each way, so a thin stream of fast smoke inside a large calm
  cloud is not told apart from the cloud, and its billows are kept too long and drawn out.
* For half a minute or so after a fire goes out, up to a tenth of the smoke shows big billows drawn out
  to more than twice their length (see [What was tested](What-was-tested.md)).
* Deep in a cloud seen from close by (past some ten metres of smoke along the line of sight) the billows
  are drawn more coarsely than those in front.
* A cloud flying fast is carried along whole between one grid and the next. Parts of it that are moving
  differently from the whole (its head and its tail, while it is being drawn out) still move in steps,
  smaller ones since the grids are made in C: a grid is then a frame or two old when it is drawn and not
  three to five. (Following the front of a fire thrown at 300 m/s frame by frame, its speed was 44% out
  from one frame to the next, taking the middle case, with the C# and 30% with the library, and the worst
  tenth of frames were out by 3.6 times the speed against 1.1 times. A rough measure, three runs each.)
* With TUFX's default smoothing between frames (TAA), anything see-through that moves across the view is
  smeared a little, this smoke included; that is TUFX's doing and its other settings do not do it.
* The few particles that stray right out of the grid's box are not drawn.
* The wind is invented and does not affect craft.
* The ground is known as heights on a lattice of 65 points each way under the cloud, half a metre apart
  under a small fire and two, four or eight metres under a large cloud: a ditch, a kerb or a step narrower
  than that is smoothed over, and a wall is taken as a steep bank one step wide. Something thin and tall (a
  mast, a flagpole) is left out on purpose. A building is taken as a hill the shape of its roof: smoke goes
  over it, not into it or under an overhang.
* Smoke lying at the top of a cliff or a wall hangs over the edge by no more than its own width; it does
  not pour to the bottom all at once, though what is heavy enough goes on down as it drifts.
* Smoke that is still hot does not follow falling ground: what a blast throws out level from the top of a
  bank goes on level. Only cooled smoke, dust and cold vapour run downhill, and slowly.
* Water gets spray and mist, not ripples or foam.
* Pieces of wreckage pass through things, and smoke takes a frame or two to notice an obstacle.
* Smoke casts no shadow on the ground and receives none from buildings.
* A burn mark is also thrown onto anything standing in it that faces the same way as the ground (the
  foot of a landing leg, say).
