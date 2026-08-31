// Dumps design/logo.af geometry to JSON for scripts/design/af-to-svg.py.
//
// The .af is the editable master; design/logo.svg is the committed, tool-neutral
// one. This is the bridge. Run it through the Affinity MCP (execute_script) with
// design/logo.af open, then run af-to-svg.py on the file it writes.
//
// Affinity scripts can only write to the Desktop, hence the destination.
const { app } = require('/application.js');
const { SolidFill } = require('/fills.js');
const { File } = require('/fs.js');

const doc = app.documents.all.find(d => String(d.path).endsWith('logo.af'));
if (!doc) throw new Error('design/logo.af is not open');

const kids = (n) => { try { return Array.from(n.children); } catch (e) { return []; } };
const nm   = (n) => { try { return n.description ?? ''; } catch (e) { return '?'; } };
const r    = (v) => Math.round(v * 10000) / 10000;
const h2   = (v) => ('0' + Math.round(v).toString(16)).slice(-2).toUpperCase();

function hex(desc) {
    try {
        const f = desc?.fill; if (!f) return null;
        const c = SolidFill.fromFill(f).colour.getRGBA8(true, null);
        return '#' + h2(c.r) + h2(c.g) + h2(c.b) + (c.alpha < 255 ? '/' + c.alpha : '');
    } catch (e) { return null; }
}

function dumpCurve(c) {
    const segs = [];
    const s = c.getPoint(c.firstOnCurvePointIndex);
    for (const b of c.beziers)
        segs.push([r(b.c1.x), r(b.c1.y), r(b.c2.x), r(b.c2.y), r(b.end.x), r(b.end.y)]);
    return { start: [r(s.x), r(s.y)], segs, closed: c.isClosed };
}

function dumpNode(n) {
    const o = { name: nm(n), type: n[Symbol.toStringTag] };
    try { const b = n.getExactSpreadBaseBox(); if (b) o.box = [r(b.x), r(b.y), r(b.width), r(b.height)]; } catch (e) {}
    try { o.xf = Array.from(n.transform.data).map(r); } catch (e) {}
    try { o.visible = n.isVisible; } catch (e) {}
    const fill = hex(n.brushFillDescriptor); if (fill) o.fill = fill;
    try {
        if (n.lineWeight) o.stroke = { w: r(n.lineWeight), cap: n.lineCap.value ?? n.lineCap,
                                       join: n.lineJoin.value ?? n.lineJoin, colour: hex(n.penFillDescriptor) };
    } catch (e) {}
    try {
        const pc = n.polyCurve;
        if (pc && pc.curveCount) { o.curves = []; for (let i = 0; i < pc.curveCount; i++) o.curves.push(dumpCurve(pc.at(i))); }
    } catch (e) {}
    o.children = kids(n).map(dumpNode);
    return o;
}

const out = JSON.stringify(dumpNode(doc.rootNode));
const dest = app.userDesktopPath + '\pgnimbus-logo-dump.json';
const f = new File(dest, 'wb');
f.writeStringAsUtf8(out);
f.close();
console.log('wrote ' + dest + ' (' + out.length + ' bytes)');
