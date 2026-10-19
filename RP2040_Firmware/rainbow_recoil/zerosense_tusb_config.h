#pragma once

// Keep Adafruit's board/CDC/HID defaults, changing only host descriptor capacity.
// The stock 256-byte shared buffer can strand enumeration for composite
// receivers, or skip a larger mouse report descriptor. No USB timing is changed.
#include "tusb_config.h"
#undef CFG_TUH_ENUMERATION_BUFSIZE
#define CFG_TUH_ENUMERATION_BUFSIZE 1024
