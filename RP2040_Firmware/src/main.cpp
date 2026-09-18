// Expose the framework library include to PlatformIO's dependency scanner;
// includes nested only through an external .ino file are not discovered.
#include <Adafruit_TinyUSB.h>

// PlatformIO builds the same canonical sketch used by Arduino IDE.
#include "../rainbow_recoil/rainbow_recoil.ino"
