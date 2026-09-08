// Site search. Modelled on fsdocs' own `content/fsdocs-search.js`, but not it:
// that script imports Fuse from esm.sh at run time and renders each result with
// an `<iconify-icon>`, which are two CDN dependencies on a site that has none.
// Fuse is vendored beside this file and the icons are gone.
//
// The index it reads is written by `build.fsx`, which merges fsdocs' generated
// `index.json` — the API reference — with an entry per authored page, so one
// box searches the whole site rather than the reference alone.
import Fuse from "/fuse.mjs";

const button = document.querySelector("#search-btn");
const dialog = document.querySelector("dialog.search");

if (button && dialog) {
    const box = dialog.querySelector("input[type=search]");
    const list = dialog.querySelector("ul");
    const empty = dialog.querySelector(".empty");

    const prompt = "Type to search the guides, the tutorial and the API.";
    let fuse = null;

    fetch("/index.json")
        .then(response => response.json())
        .then(index => {
            fuse = new Fuse(index, {
                includeScore: true,
                // `title` first and weighted hardest: searching an API reference
                // is nearly always looking for a name you half-remember, and a
                // body-text match on a common word should never outrank it.
                keys: [
                    { name: "title", weight: 3 },
                    { name: "headings", weight: 2 },
                    { name: "content", weight: 1 }
                ],
                // 0.3, chosen by measurement: at fsdocs' own 0.6 a term the
                // site has nothing on ("ultraram") still returned four
                // confident-looking pages, because fuzzy matching across an
                // 8,000-character `content` field will match nearly anything.
                // At 0.3 that returns nothing and every real term still
                // resolves, typos included.
                threshold: 0.3,
                ignoreLocation: true,
                minMatchCharLength: 2,
                ignoreFieldNorm: true
            });
        })
        // A search box that cannot search is worse than none: it invites a
        // question and then swallows it.
        .catch(() => { button.style.display = "none"; });

    const clear = () => {
        empty.style.display = "block";
        list.replaceChildren();
    };

    const render = term => {
        if (!fuse) return;
        const results = fuse.search(term, { limit: 25 });

        if (results.length === 0) {
            clear();
            empty.textContent = "Nothing matched.";
            return;
        }

        empty.style.display = "none";
        list.replaceChildren(...results.map(({ item }) => {
            const li = document.createElement("li");
            const a = document.createElement("a");
            a.href = item.uri;
            a.textContent = item.title;
            const kind = document.createElement("span");
            kind.className = "kind";
            kind.textContent = item.section || "";
            a.appendChild(kind);
            li.appendChild(a);
            return li;
        }));
    };

    let timer;
    const debounced = term => {
        clearTimeout(timer);
        timer = setTimeout(() => render(term), 150);
    };

    const open = () => { dialog.showModal(); box.focus(); };
    const close = () => { box.value = ""; empty.textContent = prompt; clear(); dialog.close(); };

    button.addEventListener("click", open);
    // Clicking the backdrop is a click on the DIALOG itself; anything inside it
    // reports the inner element as the target.
    dialog.addEventListener("click", ev => { if (ev.target === dialog) close(); });
    dialog.addEventListener("close", () => { box.value = ""; clear(); });

    box.addEventListener("input", ev => {
        const term = ev.target.value.trim();
        if (!term) { empty.textContent = prompt; clear(); } else { debounced(term); }
    });

    // "/" focuses search the way it does nearly everywhere else, but not while
    // the reader is already typing into something.
    document.addEventListener("keydown", ev => {
        const typing = ["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName);
        if (ev.key === "/" && !typing && !dialog.open) {
            ev.preventDefault();
            open();
        }
    });
}
