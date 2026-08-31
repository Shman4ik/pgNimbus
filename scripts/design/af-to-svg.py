#!/usr/bin/env python3
"""Writes design/logo.svg from a design/logo.af dump.

The .af is where the mark is drawn; this SVG is the committed master that every
other asset is generated from (see design/LOGO-ASSETS.md). Keeping the bridge in
a script rather than in someone's export settings is what stops the two from
drifting.

There is deliberately no dark variant. The mark is plated - a dark disc holding
a light field - so it carries its own contrast and reads on white, on a light
UI, on GitHub dark and on black alike. A second colourway would be a second
thing to keep in step for no gain. The one surface that genuinely needs the
palette inverted is the unplated Windows title-bar icon, and make-masters.ps1
does that swap itself, at the point of use.

    (1) open design/logo.af in Affinity
    (2) run scripts/design/dump-af.js through the Affinity MCP
    (3) python scripts/design/af-to-svg.py [dump.json]

What it guarantees about the output, because these are the rules that make the
file liftable into a sibling mark (kubeNimbus shares the broom):

  * plain <path> geometry in the root coordinate system - every node transform
    is baked into the numbers, so there is no transform/mask/use anywhere;
  * the two base circles stay <circle>, because make-masters.ps1 builds the
    transparent glyph by stripping the full-bleed one, matched on r="512";
  * colour is the two classes .ink / .paper, with the value repeated as a plain
    attribute so tools that ignore <style> still render, and so a host page can
    retheme the mark without touching the geometry.
"""
import json
import os
import sys

INK = '#242B36'
PAPER = '#F5F7FA'

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_DUMP = os.path.join(os.path.expanduser('~'), 'Desktop', 'pgnimbus-logo-dump.json')

# The one path that is paper rather than ink. Everything else about a node - its
# name, its geometry, its module - comes from the dump.
PAPER_PATHS = {'broom-grip-slot'}

HEADER = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img" aria-labelledby="logo-title">
  <title id="logo-title">pgNimbus</title>

  <!-- ============================================================
       pgNimbus logo - flattened master.

       GENERATED from design/logo.af by scripts/design/af-to-svg.py.
       Draw in the .af; this file is overwritten, so hand edits here
       are lost the next time anyone regenerates.

       Every module is plain <path> geometry in the root coordinate
       system: no <mask>, no <use>, no transform, no CSS variables.
       That is what makes it survive Inkscape / Illustrator / Figma
       and what makes each group liftable into a sibling mark.

       #base             the plate and the light field it encloses
       #mascot-elephant  the elephant
       #brand-broom      the Nimbus broom, shared across the family

       Two rules hold the drawing together. Nothing changes colour
       where it crosses the field's rim: the broom's handle and the
       tip of the trunk both carry on past the light field onto the
       plate and stay ink the whole way, readable there because of
       a .paper clearance halo drawn underneath. And each module
       carries its own clearance, so hiding #base leaves a whole
       elephant and a whole broom rather than a heap of fragments.

       Colour is two classes with the value repeated as a plain
       attribute, so tools that ignore <style> still render; CSS
       wins where it is honoured, so a host page can retheme the
       mark without touching the geometry. There is no dark
       colourway: the plate carries the mark's own contrast, so
       this one file reads on light and dark alike.
       ============================================================ -->

  <style>
    .ink     { fill: INK_COLOUR }
    .paper   { fill: PAPER_COLOUR }
    .paper-s { stroke: PAPER_COLOUR }
  </style>
""".replace('INK_COLOUR', INK).replace('PAPER_COLOUR', PAPER)


def num(v):
    """Two decimals, trailing zeros trimmed - the format the master already used."""
    s = '%.2f' % v
    s = s.rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def path_data(node):
    """The node's curves with its transform baked into the numbers."""
    a, b, tx, c, d, ty = node.get('xf', [1, 0, 0, 0, 1, 0])

    def pt(x, y):
        return (a * x + b * y + tx, c * x + d * y + ty)

    out = []
    for curve in node['curves']:
        sx, sy = pt(*curve['start'])
        out.append('M%s,%s' % (num(sx), num(sy)))
        for s in curve['segs']:
            p1, p2, p3 = pt(s[0], s[1]), pt(s[2], s[3]), pt(s[4], s[5])
            out.append('C%s,%s %s,%s %s,%s' % (num(p1[0]), num(p1[1]),
                                               num(p2[0]), num(p2[1]),
                                               num(p3[0]), num(p3[1])))
        if curve['closed']:
            out.append('Z')
    return ''.join(out)


def circle(node):
    """A base disc, kept as <circle> so make-masters.ps1 can find it by radius."""
    x, y, w, h = node['box']
    if abs(w - h) > 0.01:
        raise SystemExit('%s is not circular: %s' % (node['name'], node['box']))
    return num(x + w / 2), num(y + h / 2), num(w / 2)


def build(dump):
    spread = dump['children'][0]
    out = [HEADER]
    for group in spread['children']:
        out.append('\n  <g id="%s">' % group['name'])
        for node in group['children']:
            halos = node.get('children') or []
            if halos and 'clearance' in node['name']:
                weight = halos[0]['stroke']['w']
                out.append('    <g id="%s" class="paper paper-s" fill="%s" stroke="%s"'
                           ' stroke-width="%g" stroke-linejoin="round" stroke-linecap="round">'
                           % (node['name'], PAPER, PAPER, weight))
                for halo in halos:
                    if abs(halo['stroke']['w'] - weight) > 1e-6:
                        raise SystemExit('%s: halo weights differ (%g vs %g)'
                                         % (node['name'], halo['stroke']['w'], weight))
                    out.append('      <path d="%s"/>' % path_data(halo))
                out.append('    </g>')
            elif not node.get('curves'):
                continue
            elif node['type'] == 'ShapeNode':
                cx, cy, r = circle(node)
                is_paper = (node.get('fill') or '').upper().startswith(PAPER)
                out.append('    <circle class="%s" fill="%s" cx="%s" cy="%s" r="%s"/>'
                           % ('paper' if is_paper else 'ink', PAPER if is_paper else INK, cx, cy, r))
            else:
                is_paper = node['name'] in PAPER_PATHS
                out.append('    <path id="%s" class="%s" fill="%s" d="%s"/>'
                           % (node['name'], 'paper' if is_paper else 'ink',
                              PAPER if is_paper else INK, path_data(node)))
        out.append('  </g>')
    out.append('</svg>')
    return '\n'.join(out) + '\n'


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DUMP
    if not os.path.exists(src):
        raise SystemExit('dump not found: %s\n'
                         'Run scripts/design/dump-af.js in Affinity first.' % src)
    svg = build(json.load(open(src, encoding='utf-8')))

    dst = os.path.join(REPO, 'design', 'logo.svg')
    with open(dst, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(svg)
    print('wrote design/logo.svg (%d bytes)' % len(svg))


if __name__ == '__main__':
    main()
