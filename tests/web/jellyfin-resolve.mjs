// Lets Node load the dashboard modules the way Jellyfin serves them (plan 8, ruling R4).
// Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
//
// In the browser a module imports its siblings as './configurationpage?name=tgjelly-ui.js', the URL Jellyfin serves an
// embedded page under. Node has no such file, so this hook maps that specifier to the sibling file tgjelly-ui.js in
// the importing module's folder. Every other specifier resolves as usual.
import { register } from 'node:module';

const hooks = `
export async function resolve(specifier, context, nextResolve) {
    const match = /^\\.\\/configurationpage\\?name=([\\w.-]+)$/i.exec(specifier);
    if (match !== null && context.parentURL !== undefined) {
        return nextResolve(new URL(match[1], context.parentURL).href, context);
    }

    return nextResolve(specifier, context);
}
`;

register(`data:text/javascript,${encodeURIComponent(hooks)}`);
