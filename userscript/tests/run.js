// Button-injection tests for the userscript, run against a real DOM (jsdom).
//
// Why this exists: Jellyfin's item detail page REUSES its DOM. Opening "More Like This"
// swaps the container contents while .btnPlay and friends stay the same nodes, so a button
// injected earlier lingers. A regression here shows up as "no button until you refresh".
//
// Usage: node run.js <path-to-userscript>   (see run-tests.ps1 for the jsdom setup)
const { JSDOM } = require("jsdom");
const fs = require("fs");

const scriptPath = process.argv[2];
if (!scriptPath) {
    console.error("usage: node run.js <path-to-userscript>");
    process.exit(2);
}

const script = fs.readFileSync(scriptPath, "utf8");

const dom = new JSDOM(
    `<!DOCTYPE html><html><body>
       <div class="detailPage">
         <div class="mainDetailButtons"><button class="btnPlay emby-button">Play</button></div>
       </div>
     </body></html>`,
    { url: "http://localhost:8096/web/index.html#!/details?id=ITEM_A" }
);

const { window } = dom;
// The script talks to the local listener and the Jellyfin API; stub both out.
window.fetch = () => Promise.resolve({ ok: false, status: 0, json: () => Promise.resolve({}) });
window.alert = () => {};
window.unsafeWindow = window;
global.window = window;
global.document = window.document;
global.location = window.location;
global.MutationObserver = window.MutationObserver;
global.URLSearchParams = window.URLSearchParams;
global.unsafeWindow = window;
global.fetch = window.fetch;
global.alert = window.alert;

let pass = 0;
let fail = 0;
const check = (ok, what) => {
    console.log(`  ${ok ? "PASS" : "FAIL"}  ${what}`);
    ok ? pass++ : fail++;
};
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const buttons = () => window.document.querySelectorAll("#local-play-btn");
const buttonItem = () => {
    const btn = window.document.getElementById("local-play-btn");
    return btn ? btn.dataset.itemId : null;
};

(async () => {
    window.eval(script);
    await wait(300);

    console.log("== first visit to a detail page ==");
    check(buttons().length === 1, `one button injected (actual ${buttons().length})`);
    check(buttonItem() === "ITEM_A", `button belongs to item A (actual ${buttonItem()})`);

    console.log("\n== same item re-rendered (id unchanged) ==");
    window.document.querySelector(".mainDetailButtons").innerHTML =
        '<button class="btnPlay emby-button">Play</button>';
    await wait(400);
    check(buttons().length === 1, `still exactly one button (actual ${buttons().length})`);
    check(buttonItem() === "ITEM_A", `still item A (actual ${buttonItem()})`);

    console.log("\n== navigate to another item, DOM reused (the 'More Like This' case) ==");
    window.location.hash = "#!/details?id=ITEM_B";
    window.dispatchEvent(new window.Event("hashchange"));
    await wait(500);
    check(buttons().length === 1, `no duplicate button left behind (actual ${buttons().length})`);
    check(buttonItem() === "ITEM_B", `button follows the new item (actual ${buttonItem()})`);

    console.log("\n== whole container repainted, hash unchanged ==");
    window.document.querySelector(".mainDetailButtons").innerHTML =
        '<button class="btnPlay emby-button">Play</button>';
    await wait(400);
    check(buttons().length === 1, `observer restored the button (actual ${buttons().length})`);
    check(buttonItem() === "ITEM_B", `still item B (actual ${buttonItem()})`);

    console.log("\n== leaving the detail page ==");
    window.location.hash = "#!/home";
    window.dispatchEvent(new window.Event("hashchange"));
    await wait(400);
    check(buttons().length === 0, `button removed (actual ${buttons().length})`);

    console.log(`\n${pass} passed, ${fail} failed`);
    process.exit(fail === 0 ? 0 : 1);
})();
