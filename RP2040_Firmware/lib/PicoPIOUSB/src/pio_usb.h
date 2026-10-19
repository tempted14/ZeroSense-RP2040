
#pragma once

#include "pio_usb_configuration.h"
#include "usb_definitions.h"
#include "pio_usb_host_bus_status.h"

#ifdef __cplusplus
 extern "C" {
#endif

// Host functions
usb_device_t *pio_usb_host_init(const pio_usb_configuration_t *c);
int pio_usb_host_add_port(uint8_t pin_dp, PIO_USB_PINOUT pinout);
void pio_usb_host_task(void);
void pio_usb_host_stop(void);
void pio_usb_host_restart(void);
uint32_t pio_usb_host_get_frame_number(void);
uint32_t pio_usb_host_in_attempt_count(void);
uint32_t pio_usb_host_in_nak_count(void);
// Last failed IN header: sync[7:0], PID[15:8], address[22:16],
// send/RX-ready[23], endpoint[31:24]. Zero until a failure occurs.
uint32_t pio_usb_host_last_in_error(void);
uint32_t pio_usb_host_tx_timeout_count(void);
uint32_t pio_usb_host_rx_flag_timeout_count(void);
uint32_t pio_usb_host_rx_packet_timeout_count(void);
uint32_t pio_usb_host_rx_oversize_count(void);
uint32_t pio_usb_host_filtered_disconnect_count(void);
// Read-only sampled line/root status. It does not measure VBUS voltage.
uint32_t pio_usb_host_root_status(uint8_t root_idx);

// Call this every 1ms when skip_alarm_pool is true.
void pio_usb_host_frame(void);

// Device functions
usb_device_t *pio_usb_device_init(const pio_usb_configuration_t *c,
                                  const usb_descriptor_buffers_t *buffers);
void pio_usb_device_task(void);

// Common functions
endpoint_t *pio_usb_get_endpoint(usb_device_t *device, uint8_t idx);
int pio_usb_get_in_data(endpoint_t *ep, uint8_t *buffer, uint8_t len);
int pio_usb_set_out_data(endpoint_t *ep, const uint8_t *buffer, uint8_t len);

#ifdef __cplusplus
 }
#endif
