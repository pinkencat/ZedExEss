; -----------------------------------------------------------------------------
; BIFROST*2 ENGINE by Einar Saukas
; A Rainbow Graphics 20 Columns 8x1 Multicolor Engine for Animated Tiles
;
; To be compiled with PASMO - http://pasmo.speccy.org/
; -----------------------------------------------------------------------------

; Animation size: 2 or 4 frames per animation group
ANIM_GROUP      EQU 4

; First non-animated frame
STATIC_MIN      EQU 128

; Value subtracted from non-animated frames
STATIC_OVERLAP  EQU 128

; Default location of multicolor tiles table (16x16 pixels, 64 bytes per tile)
TILE_IMAGES     EQU 49000

; Tile rendering order (1 for sequential, 7 or 9 for distributed)
TILE_ORDER      EQU 7

; Location of the tile map (11x10=110 tiles)
TILE_MAP        EQU 65281

; Number of char rows rendered in multicolor (3-22)
; (notice that addresses from 57690+332*TOTAL_ROWS to 64994 are unused)
TOTAL_ROWS      EQU 22
