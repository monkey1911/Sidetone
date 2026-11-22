// SPDX-License-Identifier: AGPL-3.0-only
/*
 * Sidetone for macOS: the universal launcher in Sidetone.app/Contents/MacOS.
 *
 * .NET publishes one architecture at a time and a self-contained build can't be merged with lipo, so the
 * .app holds both complete builds (Contents/Resources/arm64 and x64). This launcher is built for both
 * (clang -arch arm64 -arch x86_64); macOS runs its arm64 half on Apple silicon and its x86_64 half on
 * Intel, and it runs the matching build's program of the same name as itself (sidetone or sidetone-cli),
 * in the same process (execv), so the app's Info.plist, icon and microphone permission still apply.
 */

#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#if defined(__arm64__)
#define ARCH "arm64"
#else
#define ARCH "x64"
#endif

int main(int argc, char *argv[])
{
    char exe[PATH_MAX], real[PATH_MAX], name[PATH_MAX], target[PATH_MAX];
    uint32_t size = sizeof exe;
    if (_NSGetExecutablePath(exe, &size) != 0 || realpath(exe, real) == NULL) {
        fprintf(stderr, "sidetone: can't find where Sidetone.app is\n");
        return 127;
    }
    // the program's name from how it was started (a symlink called sidetone-cli runs sidetone-cli), the folder from the real path
    strncpy(name, basename(exe), sizeof name - 1);
    name[sizeof name - 1] = '\0';
    snprintf(target, sizeof target, "%s/../Resources/" ARCH "/%s", dirname(real), name);
    argv[0] = target;
    execv(target, argv);
    perror(target);
    return 127;
}
