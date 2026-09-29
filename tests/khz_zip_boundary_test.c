#include "khz_xlsx_reader.h"
#include <stdio.h>
#include <string.h>
static unsigned char archive[1024];
static size_t central[2], local[2], end_record, length;
static void u16(size_t p, unsigned v) { archive[p]=(unsigned char)v; archive[p+1]=(unsigned char)(v>>8); }
static void u32(size_t p, uint32_t v) { u16(p,v&65535u); u16(p+2,v>>16); }
static void build(void)
{
    const char *names[]={"one.xml","two.xml"};
    size_t p=0;
    memset(archive,0,sizeof archive);
    for (size_t i=0;i<2;++i) {
        local[i]=p; u32(p,KHZ_XLSX_SIG_LOCAL); u16(p+4,20);
        u32(p+14,UINT32_C(0x8cdc1683)); u32(p+18,1); u32(p+22,1);
        u16(p+26,7); memcpy(archive+p+30,names[i],7); archive[p+37]='x'; p+=38;
    }
    for (size_t i=0;i<2;++i) {
        central[i]=p; u32(p,KHZ_XLSX_SIG_CENTRAL); u16(p+4,20); u16(p+6,20);
        u32(p+16,UINT32_C(0x8cdc1683)); u32(p+20,1); u32(p+24,1);
        u16(p+28,7); u32(p+42,(uint32_t)local[i]); memcpy(archive+p+46,names[i],7); p+=53;
    }
    end_record=p; u32(p,KHZ_XLSX_SIG_EOCD); u16(p+8,2); u16(p+10,2);
    u32(p+12,106); u32(p+16,76); length=p+22;
}
static int expect(const char *name, KhzSheetStatus expected)
{
    KhzArena arena; KhzXlsxReader reader;
    if (khz_arena_init(&arena,(size_t)1<<20)!=KHZ_ARENA_OK) return 1;
    KhzSheetStatus got=khz_xlsx_reader_init(&reader,&arena);
    if (got==KHZ_SHEET_OK) got=khz_xlsx_reader_load(&reader,archive,length);
    int failed=got!=expected || (got!=KHZ_SHEET_OK && khz_arena_used(&arena)!=0);
    printf("%s %s status=%d expected=%d\n",failed?"FAIL":"PASS",name,got,expected);
    khz_arena_destroy(&arena); return failed;
}
int main(void)
{
    int failures=0;
    build(); failures+=expect("valid stored entries",KHZ_SHEET_OK);
    build(); u32(local[0]+14,0); failures+=expect("local CRC conflict",KHZ_SHEET_ERR_FORMAT);
    build(); u32(local[0]+18,2); failures+=expect("local size conflict",KHZ_SHEET_ERR_FORMAT);
    build(); u16(local[0]+6,8); failures+=expect("local flags conflict",KHZ_SHEET_ERR_FORMAT);
    build(); u16(local[0]+6,1); u16(central[0]+8,1);
    failures+=expect("encryption unsupported",KHZ_SHEET_ERR_UNSUPPORTED);
    build(); u16(central[0]+34,1); failures+=expect("member on another disk",KHZ_SHEET_ERR_UNSUPPORTED);
    build(); memcpy(archive+local[1]+30,"one.xml",7); memcpy(archive+central[1]+46,"one.xml",7);
    failures+=expect("duplicate member",KHZ_SHEET_ERR_FORMAT);
    build(); memcpy(archive+local[1]+30,"ONE.xml",7); memcpy(archive+central[1]+46,"ONE.xml",7);
    failures+=expect("ASCII case collision",KHZ_SHEET_ERR_FORMAT);
    build(); u32(central[1]+42,0); memcpy(archive+central[1]+46,"one.xml",7);
    failures+=expect("shared local record",KHZ_SHEET_ERR_FORMAT);
    build(); memcpy(archive+local[0]+30,"../a.xx",7); memcpy(archive+central[0]+46,"../a.xx",7);
    failures+=expect("parent path",KHZ_SHEET_ERR_FORMAT);
    build(); archive[local[0]+31]=0; archive[central[0]+47]=0;
    failures+=expect("embedded NUL",KHZ_SHEET_ERR_FORMAT);
    build(); archive[local[0]+31]='\\'; archive[central[0]+47]='\\';
    failures+=expect("backslash ambiguity",KHZ_SHEET_ERR_FORMAT);
    build();
    u32(local[0]+18,39); u32(local[0]+22,39); u32(central[0]+20,39); u32(central[0]+24,39);
    uint32_t crc=khz_xlsx_reader_crc32(archive+37,39);
    u32(local[0]+14,crc); u32(central[0]+16,crc);
    failures+=expect("overlapping different members",KHZ_SHEET_ERR_FORMAT);
    build(); u16(local[0]+8,8); u16(central[0]+10,8);
    u32(local[0]+22,1001); u32(central[0]+24,1001);
    failures+=expect("compression ratio ceiling before decode",KHZ_SHEET_ERR_LIMIT);
    for (int signature=0; signature<2; ++signature) {
        build();
        size_t descriptor_bytes=signature?16u:12u;
        size_t descriptor=76;
        memmove(archive+76+descriptor_bytes,archive+76,length-76);
        central[0]+=descriptor_bytes; central[1]+=descriptor_bytes; end_record+=descriptor_bytes;
        length+=descriptor_bytes;
        u32(end_record+16,(uint32_t)(76+descriptor_bytes));
        u16(local[1]+6,8); u16(central[1]+8,8);
        u32(local[1]+14,0); u32(local[1]+18,0); u32(local[1]+22,0);
        if (signature) { u32(descriptor,UINT32_C(0x08074b50)); descriptor+=4; }
        u32(descriptor,UINT32_C(0x8cdc1683)); u32(descriptor+4,1); u32(descriptor+8,1);
        failures+=expect(signature?"signed descriptor":"unsigned descriptor",KHZ_SHEET_OK);
        u32(descriptor+8,2);
        failures+=expect("conflicting descriptor",KHZ_SHEET_ERR_FORMAT);
    }
    printf("failures=%d\n",failures);
    return failures?1:0;
}
