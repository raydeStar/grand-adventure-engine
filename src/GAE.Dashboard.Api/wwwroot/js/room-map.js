/**
 * Room map renderer using rot.js Display.
 * Generates a small ASCII-style map from room data.
 */
const RoomMap = (function () {
  'use strict';

  const MAP_W = 17;
  const MAP_H = 11;
  const CENTER_X = Math.floor(MAP_W / 2);
  const CENTER_Y = Math.floor(MAP_H / 2);

  const WALL = '#';
  const FLOOR = '\u00b7'; // middle dot
  const PLAYER = '@';

  const EXIT_ARROWS = {
    north: { dx: 0, dy: -1, sym: '\u2191' },    // ↑
    south: { dx: 0, dy: 1, sym: '\u2193' },      // ↓
    east:  { dx: 1, dy: 0, sym: '\u2192' },      // →
    west:  { dx: -1, dy: 0, sym: '\u2190' },     // ←
    up:    { dx: 1, dy: -1, sym: '\u2197' },      // ↗
    down:  { dx: -1, dy: 1, sym: '\u2199' },     // ↙
    debug: { dx: 1, dy: 1, sym: '\u2666' }       // ♦
  };

  const COLORS = {
    wall:    '#1a2a1a',
    floor:   '#223322',
    player:  '#33ff66',
    npc:     '#ccaa33',
    hostile: '#ff4444',
    item:    '#44aaff',
    exit:    '#33ff66',
    bg:      '#050505'
  };

  function render(container, room) {
    if (!container || !room || typeof ROT === 'undefined') return;

    container.innerHTML = '';
    container.classList.remove('hidden');

    const fontSize = 12;
    const display = new ROT.Display({
      width: MAP_W,
      height: MAP_H,
      fontSize: fontSize,
      fontFamily: "'IBM Plex Mono', Consolas, monospace",
      bg: COLORS.bg,
      forceSquareRatio: true
    });

    container.appendChild(display.getContainer());

    // Draw walls and floor
    for (let y = 0; y < MAP_H; y++) {
      for (let x = 0; x < MAP_W; x++) {
        const isWall = x === 0 || x === MAP_W - 1 || y === 0 || y === MAP_H - 1;
        if (isWall) {
          display.draw(x, y, WALL, COLORS.wall, COLORS.bg);
        } else {
          display.draw(x, y, FLOOR, COLORS.floor, COLORS.bg);
        }
      }
    }

    // Draw exits as openings in the walls
    const exits = Object.entries(room.exits || {});
    exits.forEach(([direction]) => {
      const def = EXIT_ARROWS[direction.toLowerCase()];
      if (!def) return;

      let ex, ey;
      if (direction === 'north') {
        ex = CENTER_X; ey = 0;
      } else if (direction === 'south') {
        ex = CENTER_X; ey = MAP_H - 1;
      } else if (direction === 'east') {
        ex = MAP_W - 1; ey = CENTER_Y;
      } else if (direction === 'west') {
        ex = 0; ey = CENTER_Y;
      } else if (direction === 'up') {
        ex = MAP_W - 2; ey = 1;
      } else if (direction === 'down') {
        ex = 1; ey = MAP_H - 2;
      } else {
        ex = MAP_W - 2; ey = MAP_H - 2;
      }

      display.draw(ex, ey, def.sym, COLORS.exit, COLORS.bg);
    });

    // Draw items around the room
    const items = room.items || [];
    const itemPositions = [
      [CENTER_X - 3, CENTER_Y - 2],
      [CENTER_X + 3, CENTER_Y + 2],
      [CENTER_X - 2, CENTER_Y + 2],
      [CENTER_X + 2, CENTER_Y - 2],
      [CENTER_X - 4, CENTER_Y]
    ];
    items.forEach((item, i) => {
      if (i >= itemPositions.length) return;
      const [ix, iy] = itemPositions[i];
      if (ix > 0 && ix < MAP_W - 1 && iy > 0 && iy < MAP_H - 1) {
        display.draw(ix, iy, '*', COLORS.item, COLORS.bg);
      }
    });

    // Draw NPCs
    const npcs = room.npcs || [];
    const npcPositions = [
      [CENTER_X + 2, CENTER_Y - 1],
      [CENTER_X - 2, CENTER_Y + 1],
      [CENTER_X + 3, CENTER_Y],
      [CENTER_X - 3, CENTER_Y - 1],
      [CENTER_X, CENTER_Y + 2]
    ];
    npcs.forEach((npc, i) => {
      if (i >= npcPositions.length) return;
      const [nx, ny] = npcPositions[i];
      if (nx > 0 && nx < MAP_W - 1 && ny > 0 && ny < MAP_H - 1) {
        const letter = (npc.name || 'N').charAt(0).toUpperCase();
        const color = npc.isHostile ? COLORS.hostile : COLORS.npc;
        display.draw(nx, ny, letter, color, COLORS.bg);
      }
    });

    // Draw player in center
    display.draw(CENTER_X, CENTER_Y, PLAYER, COLORS.player, COLORS.bg);
  }

  function clear(container) {
    if (!container) return;
    container.innerHTML = '';
    container.classList.add('hidden');
  }

  return { render, clear };
})();
