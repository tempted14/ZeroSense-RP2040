#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace ZeroSenseProtocol {

// Core-0-only reply queue. Enqueue whole messages or reject them; never block
// HID output waiting for the CDC reader. A missing ACK uses existing retries.
template <size_t Capacity>
class OutputQueue {
public:
    bool enqueue(const char* bytes, size_t length) {
        if (length > Capacity - size_) {
            ++droppedMessages;
            return false;
        }
        for (size_t i = 0; i < length; ++i) {
            data_[(head_ + size_ + i) % Capacity] = static_cast<uint8_t>(bytes[i]);
        }
        size_ += length;
        return true;
    }

    // One bounded write attempt. A full/stalled FIFO returns immediately and
    // a short write consumes only the bytes actually accepted by the driver.
    template <typename Writer>
    size_t drain(size_t available, size_t budget, Writer write) {
        const size_t count = std::min({size_, Capacity - head_, available, budget});
        if (count == 0) return 0;
        const size_t written = std::min(count, write(data_ + head_, count));
        head_ = (head_ + written) % Capacity;
        size_ -= written;
        return written;
    }

    void clear() { head_ = 0; size_ = 0; }
    size_t size() const { return size_; }
    uint32_t droppedMessages = 0;

private:
    uint8_t data_[Capacity] = {};
    size_t head_ = 0;
    size_t size_ = 0;
};

} // namespace ZeroSenseProtocol
