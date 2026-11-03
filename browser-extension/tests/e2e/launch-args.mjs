// Chrome refuses to start its sandbox as root, which is how Linux containers and
// some CI runners run. Add --no-sandbox only there; Windows and normal users keep it.
export const sandboxArgs = typeof process.getuid === "function" && process.getuid() === 0 ? ["--no-sandbox"] : [];
