# Pmod I2S2 on KV260 carrier card connector J2 — ADC→DAC loopback.
#
# The DAC (line-out) is on the Pmod TOP ROW (J2.1-4), the ADC (line-in) on
# the BOTTOM ROW (J2.7-10). Each converter has its own MCLK/LRCK/SCLK pins,
# so the master clocks are driven to BOTH rows (mclk/lrclk/sclk to the DAC,
# mclk2/lrclk2/sclk2 to the ADC). (Digilent Pmod I2S2 reference manual.)
#
# J2 pin -> carrier net -> K26 package pin (cross-checked: Xilinx Kria-PYNQ
# base.xdc + kv260_som part0_pins.xml). Bank 45 VCCO = 3.3 V => LVCMOS33.
#   top row    J2.1 HDA11 H12   J2.2 HDA12 E10   J2.3 HDA13 D10   J2.4 HDA14 C11
#   bottom row J2.7 HDA15 B10   J2.8 HDA16 E12   J2.9 HDA17 D11   J2.10 HDA18 B11

# --- Top row: D/A (line out) ---
set_property -dict {PACKAGE_PIN H12 IOSTANDARD LVCMOS33} [get_ports mclk]    ;# J2.1  D/A MCLK
set_property -dict {PACKAGE_PIN E10 IOSTANDARD LVCMOS33} [get_ports lrclk]   ;# J2.2  D/A LRCK
set_property -dict {PACKAGE_PIN D10 IOSTANDARD LVCMOS33} [get_ports sclk]    ;# J2.3  D/A SCLK
set_property -dict {PACKAGE_PIN C11 IOSTANDARD LVCMOS33} [get_ports sdin]    ;# J2.4  D/A SDIN

# --- Bottom row: A/D (line in) ---
set_property -dict {PACKAGE_PIN B10 IOSTANDARD LVCMOS33} [get_ports mclk2]   ;# J2.7  A/D MCLK
set_property -dict {PACKAGE_PIN E12 IOSTANDARD LVCMOS33} [get_ports lrclk2]  ;# J2.8  A/D LRCK
set_property -dict {PACKAGE_PIN D11 IOSTANDARD LVCMOS33} [get_ports sclk2]   ;# J2.9  A/D SCLK
set_property -dict {PACKAGE_PIN B11 IOSTANDARD LVCMOS33} [get_ports sdout]   ;# J2.10 A/D SDOUT (input)
