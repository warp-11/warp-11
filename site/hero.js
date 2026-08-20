// The front page's warp jump.
//
// Points -> streaks -> lanes. In Star Trek warp 10 is the asymptote: infinite
// velocity, every point in the universe at once. Warp 11 is past it, and what
// lies past "everywhere at once" is not a longer streak — it is a datapath,
// every lane advancing on one clock. So the animation ends where the pitch
// does: 104 lanes, all of them moving, which is the argument against the CPU's
// one-thing-at-a-time.

(() => {
    const canvas = document.getElementById('warp');
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    const COUNT = 900;
    const LANES = 22;
    const DRIFT = 0.9; // seconds adrift before the engines light
    const RUSH = 1.5; // seconds of acceleration
    const SNAP = 1.15; // seconds for the streaks to resolve into lanes

    const SKY = '#05070f';
    const STAR = [216, 229, 255];
    const CYAN = [86, 208, 255];
    const AMBER = [255, 185, 107];

    const mix = (a, b, t) => a + (b - a) * t;
    const blend = (a, b, t) => [mix(a[0], b[0], t), mix(a[1], b[1], t), mix(a[2], b[2], t)];
    const rgba = (c, a) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
    const rand = (a, b) => a + Math.random() * (b - a);
    const easeIn = t => t * t * t;
    const easeInOut = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

    let W = 0;
    let H = 0;
    let cx = 0;
    let cy = 0;
    let dash = 0;
    let clock = 0;
    let morph = 0;
    let snapped = false;

    // Lane colour: mostly cyan, an amber lane every fifth, so the settled state
    // reads as a structured datapath rather than a gradient.
    const laneTint = i => (i % 5 === 2 ? AMBER : CYAN);
    const laneY = i => H * 0.14 + ((H * 0.72) * i) / (LANES - 1);

    // A starfield dense enough to rush is far too dense to stand still in: at
    // this count every lane would fill solid and the dashes would merge into one
    // line. Most of the field fades out over the snap, and what is left reads as
    // packets moving.
    const stars = Array.from({ length: COUNT }, () => ({
        x: rand(-1, 1),
        y: rand(-1, 1),
        z: rand(0.08, 1),
        keep: Math.random() < 0.4,
        lane: 0,
        lx: 0,
        fx: 0,
        fy: 0,
        ftx: 0,
        fty: 0
    }));

    // The vanishing point sits right of centre: the copy lives on the left, and
    // the convergence wants clear space to converge into.
    const project = (s, z) => [cx + (s.x * W * 0.62) / z, cy + (s.y * H * 0.62) / z];

    const resize = () => {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const rect = canvas.getBoundingClientRect();
        W = Math.max(1, rect.width);
        H = Math.max(1, rect.height);
        canvas.width = Math.round(W * dpr);
        canvas.height = Math.round(H * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        cx = W * 0.62;
        cy = H * 0.5;
        dash = W * 0.028;
        if (snapped) for (const s of stars) s.lx = rand(-dash, W);
    };

    const freeze = () => {
        snapped = true;
        for (const s of stars) {
            const [px, py] = project(s, s.z);
            const [tx, ty] = project(s, Math.min(1.4, s.z + 0.11));
            s.fx = px;
            s.fy = py;
            s.ftx = tx;
            s.fty = ty;
            s.lx = px;
            // Each streak lands in the lane it was already closest to, so the
            // snap reads as convergence rather than teleportation. Clamping the
            // ones flung past the top and bottom edges would pile the whole
            // outer field into the first and last lane and light them solid, so
            // those are scattered instead.
            const near = Math.round(((py - H * 0.14) / (H * 0.72)) * (LANES - 1));
            s.lane =
                isFinite(near) && near >= 0 && near < LANES
                    ? near
                    : Math.floor(Math.random() * LANES);
        }
    };

    const step = dt => {
        clock += dt;

        if (!snapped) {
            const t = Math.min(1, Math.max(0, (clock - DRIFT) / RUSH));
            const v = mix(0.06, 2.7, easeIn(t));
            for (const s of stars) {
                s.z -= v * dt;
                if (s.z < 0.03) {
                    s.x = rand(-1, 1);
                    s.y = rand(-1, 1);
                    s.z = 1;
                }
                s.trail = Math.min(1.4, s.z + v * 0.055);
            }
            if (clock >= DRIFT + RUSH) freeze();
            return;
        }

        morph = Math.min(1, morph + dt / SNAP);
        const flow = W * 0.17 * dt;
        for (const s of stars) {
            s.lx += flow;
            if (s.lx - dash > W) s.lx -= W + dash * 2;
        }
    };

    const draw = () => {
        ctx.fillStyle = SKY;
        ctx.fillRect(0, 0, W, H);
        ctx.lineCap = 'round';

        const m = snapped ? easeInOut(morph) : 0;

        if (m > 0.01) {
            ctx.lineWidth = 1;
            for (let i = 0; i < LANES; i++) {
                ctx.strokeStyle = rgba(laneTint(i), 0.085 * m);
                ctx.beginPath();
                ctx.moveTo(0, laneY(i));
                ctx.lineTo(W, laneY(i));
                ctx.stroke();
            }
            ctx.strokeStyle = rgba(CYAN, 0.07 * m);
            for (let k = 1; k < 7; k++) {
                const x = (W * k) / 7;
                ctx.beginPath();
                ctx.moveTo(x, H * 0.1);
                ctx.lineTo(x, H * 0.9);
                ctx.stroke();
            }
        }

        for (const s of stars) {
            let hx;
            let hy;
            let tx;
            let ty;
            let width;
            let alpha;
            let tint;

            if (!snapped) {
                [hx, hy] = project(s, s.z);
                [tx, ty] = project(s, s.trail);
                width = mix(1.1, 2.4, 1 - s.z);
                alpha = Math.min(1, (1 - s.z) * 1.1 + 0.45);
                tint = STAR;
            } else {
                const [px, py] = [s.fx, s.fy];
                const ly = laneY(s.lane);
                hx = mix(px, s.lx, m);
                hy = mix(py, ly, m);
                tx = mix(s.ftx, s.lx - dash, m);
                ty = mix(s.fty, ly, m);
                width = mix(1.8, 2.4, m);
                alpha = mix(0.9, s.keep ? 0.78 : 0, m);
                tint = blend(STAR, laneTint(s.lane), m);
            }

            ctx.strokeStyle = rgba(tint, alpha);
            ctx.lineWidth = width;
            ctx.beginPath();
            ctx.moveTo(tx, ty);
            ctx.lineTo(hx, hy);
            ctx.stroke();
        }
    };

    let last = 0;
    let running = false;

    const frame = now => {
        if (!running) return;
        const dt = Math.min(0.05, (now - last) / 1000 || 0);
        last = now;
        step(dt);
        draw();
        requestAnimationFrame(frame);
    };

    const start = () => {
        if (running) return;
        running = true;
        last = performance.now();
        requestAnimationFrame(frame);
    };

    window.addEventListener('resize', () => {
        resize();
        if (!running) draw();
    });

    resize();

    if (still) {
        // The destination without the journey: lanes, already flowing, one frame.
        clock = DRIFT + RUSH;
        freeze();
        morph = 1;
        for (const s of stars) s.lx = rand(-dash, W);
        draw();
        return;
    }

    // Nothing to animate while the hero is off screen.
    if ('IntersectionObserver' in window) {
        new IntersectionObserver(entries => {
            for (const e of entries) {
                if (e.isIntersecting) start();
                else running = false;
            }
        }, { threshold: 0 }).observe(canvas);
    } else {
        start();
    }
})();
