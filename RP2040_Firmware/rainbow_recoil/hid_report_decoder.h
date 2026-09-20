#pragma once

#include <cstdint>
#include <cstring>

// This decoder deliberately has no Arduino/TinyUSB dependencies. Firmware and
// native replay tests compile the exact same parser and report-decoding code.
namespace ZeroSenseHid {

constexpr uint8_t MaximumLayouts = 8;
constexpr uint8_t MaximumFields = 16;

enum class FieldKind : uint8_t { Button, X, Y, Wheel, Pan };

struct Field {
    uint16_t bitOffset;
    uint8_t bitSize;
    FieldKind kind;
    uint8_t buttonMask;
    bool isSigned;
};

struct Layout {
    bool used;
    uint8_t reportId;
    uint8_t fieldCount;
    Field fields[MaximumFields];
};

struct Decoder {
    Layout layouts[MaximumLayouts];
};

struct DecodedReport {
    uint8_t buttons;
    uint8_t reportedButtonMask;
    int32_t deltaX;
    int32_t deltaY;
    int32_t wheel;
    int32_t pan;
};

struct GlobalState {
    uint16_t usagePage;
    int32_t logicalMinimum;
    uint8_t reportSize;
    uint8_t reportCount;
    uint8_t reportId;
};

inline int32_t signedValue(uint32_t value, uint8_t bitSize) {
    if (bitSize == 0) {
        return 0;
    }
    if (bitSize == 32) {
        int32_t result = 0;
        std::memcpy(&result, &value, sizeof(result));
        return result;
    }
    const uint32_t signBit = 1UL << (bitSize - 1);
    const uint32_t mask = (1UL << bitSize) - 1UL;
    value &= mask;
    return (value & signBit) != 0
        ? static_cast<int32_t>(value | ~mask)
        : static_cast<int32_t>(value);
}

inline uint32_t itemValue(const uint8_t* data, uint8_t size) {
    uint32_t value = 0;
    for (uint8_t index = 0; index < size; ++index) {
        value |= static_cast<uint32_t>(data[index]) << (index * 8);
    }
    return value;
}

inline uint32_t readBits(
    const uint8_t* report,
    uint16_t reportLength,
    uint16_t bitOffset,
    uint8_t bitSize,
    bool& valid) {
    if (bitSize == 0 || bitSize > 32 ||
        static_cast<uint32_t>(bitOffset) + bitSize >
            static_cast<uint32_t>(reportLength) * 8U) {
        valid = false;
        return 0;
    }
    uint32_t value = 0;
    for (uint8_t bit = 0; bit < bitSize; ++bit) {
        const uint16_t sourceBit = bitOffset + bit;
        if ((report[sourceBit / 8] & (1U << (sourceBit % 8))) != 0) {
            value |= 1UL << bit;
        }
    }
    return value;
}

inline Layout* findOrAddLayout(Decoder& decoder, uint8_t reportId) {
    for (auto& layout : decoder.layouts) {
        if (layout.used && layout.reportId == reportId) {
            return &layout;
        }
    }
    for (auto& layout : decoder.layouts) {
        if (!layout.used) {
            layout.used = true;
            layout.reportId = reportId;
            layout.fieldCount = 0;
            return &layout;
        }
    }
    return nullptr;
}

inline bool addField(
    Decoder& decoder,
    uint8_t reportId,
    uint16_t bitOffset,
    uint8_t bitSize,
    FieldKind kind,
    uint8_t buttonMask,
    bool isSigned) {
    Layout* layout = findOrAddLayout(decoder, reportId);
    if (layout == nullptr || layout->fieldCount >= MaximumFields ||
        bitSize == 0 || bitSize > 32) {
        return false;
    }
    layout->fields[layout->fieldCount++] = {
        bitOffset, bitSize, kind, buttonMask, isSigned
    };
    return true;
}

inline uint32_t qualifiedUsage(uint16_t usagePage, uint32_t usage) {
    return usage > 0xffffU
        ? usage
        : (static_cast<uint32_t>(usagePage) << 16) | usage;
}

inline bool parseDescriptor(
    Decoder& decoder,
    const uint8_t* descriptor,
    uint16_t descriptorLength,
    bool& hadMouseApplication) {
    std::memset(&decoder, 0, sizeof(decoder));
    hadMouseApplication = false;
    if (descriptor == nullptr || descriptorLength == 0) {
        return false;
    }

    GlobalState global = {};
    GlobalState globalStack[4] = {};
    uint8_t globalDepth = 0;
    uint16_t bitOffsets[256] = {};
    uint32_t usages[24] = {};
    uint8_t usageCount = 0;
    uint32_t usageMinimum = 0;
    uint32_t usageMaximum = 0;
    bool hasUsageMinimum = false;
    bool hasUsageMaximum = false;
    bool mouseCollectionStack[12] = {};
    uint8_t collectionDepth = 0;
    bool parsedField = false;

    const auto clearLocals = [&]() {
        usageCount = 0;
        usageMinimum = 0;
        usageMaximum = 0;
        hasUsageMinimum = false;
        hasUsageMaximum = false;
    };

    uint16_t offset = 0;
    while (offset < descriptorLength) {
        const uint8_t prefix = descriptor[offset++];
        if (prefix == 0xfe) {
            if (offset + 2 > descriptorLength) return false;
            const uint8_t longSize = descriptor[offset];
            offset += 2;
            if (offset + longSize > descriptorLength) return false;
            offset += longSize;
            continue;
        }
        uint8_t itemSize = prefix & 0x03U;
        itemSize = itemSize == 3 ? 4 : itemSize;
        if (offset + itemSize > descriptorLength) return false;
        const uint8_t itemType = (prefix >> 2) & 0x03U;
        const uint8_t itemTag = (prefix >> 4) & 0x0fU;
        const uint32_t value = itemValue(descriptor + offset, itemSize);
        const int32_t signedItemValue = signedValue(value, itemSize * 8);
        offset += itemSize;

        if (itemType == 1) {
            switch (itemTag) {
                case 0: global.usagePage = static_cast<uint16_t>(value); break;
                case 1: global.logicalMinimum = signedItemValue; break;
                case 7:
                    if (value == 0 || value > 32) return false;
                    global.reportSize = static_cast<uint8_t>(value);
                    break;
                case 8:
                    if (value == 0 || value > 255) return false;
                    global.reportId = static_cast<uint8_t>(value);
                    break;
                case 9:
                    if (value == 0 || value > 255) return false;
                    global.reportCount = static_cast<uint8_t>(value);
                    break;
                case 10:
                    if (globalDepth >= 4) return false;
                    globalStack[globalDepth++] = global;
                    break;
                case 11:
                    if (globalDepth == 0) return false;
                    global = globalStack[--globalDepth];
                    break;
                default: break;
            }
            continue;
        }
        if (itemType == 2) {
            switch (itemTag) {
                case 0:
                    if (usageCount >= 24) return false;
                    usages[usageCount++] = qualifiedUsage(global.usagePage, value);
                    break;
                case 1:
                    usageMinimum = qualifiedUsage(global.usagePage, value);
                    hasUsageMinimum = true;
                    break;
                case 2:
                    usageMaximum = qualifiedUsage(global.usagePage, value);
                    hasUsageMaximum = true;
                    break;
                default: break;
            }
            continue;
        }
        if (itemType != 0) continue;

        const uint32_t firstUsage = usageCount > 0
            ? usages[0]
            : hasUsageMinimum ? usageMinimum : 0;
        if (itemTag == 10) {
            const bool parentIsMouse = collectionDepth > 0 &&
                mouseCollectionStack[collectionDepth - 1];
            const bool isMouseApplication = value == 1 &&
                firstUsage == 0x00010002UL;
            hadMouseApplication = hadMouseApplication || isMouseApplication;
            if (collectionDepth >= 12) return false;
            mouseCollectionStack[collectionDepth++] =
                parentIsMouse || isMouseApplication;
            clearLocals();
            continue;
        }
        if (itemTag == 12) {
            if (collectionDepth == 0) return false;
            --collectionDepth;
            clearLocals();
            continue;
        }
        if (itemTag == 8) {
            const uint16_t startingBit = bitOffsets[global.reportId];
            const bool inMouseCollection = collectionDepth > 0 &&
                mouseCollectionStack[collectionDepth - 1];
            const bool isData = (value & 0x01U) == 0;
            const bool isVariable = (value & 0x02U) != 0;
            const bool isRelative = (value & 0x04U) != 0;
            if (inMouseCollection && isData && isVariable) {
                for (uint8_t index = 0; index < global.reportCount; ++index) {
                    uint32_t usage = 0;
                    if (index < usageCount) {
                        usage = usages[index];
                    } else if (hasUsageMinimum && hasUsageMaximum &&
                        usageMinimum + index <= usageMaximum) {
                        usage = usageMinimum + index;
                    } else if (usageCount > 0) {
                        usage = usages[usageCount - 1];
                    }
                    const uint16_t page = static_cast<uint16_t>(usage >> 16);
                    const uint16_t id = static_cast<uint16_t>(usage);
                    const uint16_t fieldOffset = startingBit +
                        static_cast<uint16_t>(index) * global.reportSize;
                    bool added = false;
                    if (page == 0x09 && id >= 1 && id <= 8) {
                        added = addField(
                            decoder, global.reportId, fieldOffset,
                            global.reportSize, FieldKind::Button,
                            static_cast<uint8_t>(1U << (id - 1)), false);
                    } else if (page == 0x01 && isRelative) {
                        FieldKind kind = FieldKind::X;
                        bool supported = true;
                        if (id == 0x30) kind = FieldKind::X;
                        else if (id == 0x31) kind = FieldKind::Y;
                        else if (id == 0x38) kind = FieldKind::Wheel;
                        else supported = false;
                        if (supported) {
                            added = addField(
                                decoder, global.reportId, fieldOffset,
                                global.reportSize, kind, 0,
                                global.logicalMinimum < 0);
                        }
                    } else if (page == 0x0c && id == 0x0238 && isRelative) {
                        added = addField(
                            decoder, global.reportId, fieldOffset,
                            global.reportSize, FieldKind::Pan, 0,
                            global.logicalMinimum < 0);
                    }
                    parsedField = parsedField || added;
                }
            }
            const uint32_t endingBit = static_cast<uint32_t>(startingBit) +
                static_cast<uint32_t>(global.reportSize) * global.reportCount;
            if (endingBit > UINT16_MAX) return false;
            bitOffsets[global.reportId] = static_cast<uint16_t>(endingBit);
        }
        clearLocals();
    }
    return parsedField && collectionDepth == 0 && globalDepth == 0;
}

inline void configureBootMouse(Decoder& decoder) {
    std::memset(&decoder, 0, sizeof(decoder));
    for (uint8_t button = 0; button < 8; ++button) {
        addField(
            decoder, 0, button, 1, FieldKind::Button,
            static_cast<uint8_t>(1U << button), false);
    }
    addField(decoder, 0, 8, 8, FieldKind::X, 0, true);
    addField(decoder, 0, 16, 8, FieldKind::Y, 0, true);
}

inline bool decodeReport(
    const Decoder& decoder,
    const uint8_t* report,
    uint16_t length,
    DecodedReport& decoded) {
    decoded = {};
    if (report == nullptr || length == 0) return false;

    const Layout* layout = nullptr;
    const uint8_t* payload = report;
    uint16_t payloadLength = length;
    for (const auto& candidate : decoder.layouts) {
        if (!candidate.used) continue;
        if (candidate.reportId == 0) {
            if (layout == nullptr) layout = &candidate;
        } else if (report[0] == candidate.reportId) {
            layout = &candidate;
            payload = report + 1;
            payloadLength = length - 1;
            break;
        }
    }
    if (layout == nullptr) return false;

    for (uint8_t index = 0; index < layout->fieldCount; ++index) {
        const Field& field = layout->fields[index];
        bool valid = true;
        const uint32_t raw = readBits(
            payload, payloadLength, field.bitOffset, field.bitSize, valid);
        if (!valid) return false;
        const int32_t value = field.isSigned
            ? signedValue(raw, field.bitSize)
            : static_cast<int32_t>(raw);
        switch (field.kind) {
            case FieldKind::Button:
                decoded.reportedButtonMask |= field.buttonMask;
                if (value != 0) decoded.buttons |= field.buttonMask;
                break;
            case FieldKind::X: decoded.deltaX += value; break;
            case FieldKind::Y: decoded.deltaY += value; break;
            case FieldKind::Wheel: decoded.wheel += value; break;
            case FieldKind::Pan: decoded.pan += value; break;
        }
    }
    return true;
}

} // namespace ZeroSenseHid
