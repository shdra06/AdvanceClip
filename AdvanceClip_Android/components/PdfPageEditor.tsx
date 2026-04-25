// PdfPageEditor — Modal component for selecting, reordering, and extracting PDF pages
import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, Modal, TouchableOpacity, ScrollView, ActivityIndicator,
  ToastAndroid, Platform, StyleSheet, Alert,
} from 'react-native';
import * as FileSystem from 'expo-file-system';
import * as Sharing from 'expo-sharing';
import { getPdfPageInfo, extractPages, mergePdfs } from '../utils/pdfUtils';
import { IconSymbol } from '@/components/ui/icon-symbol';

interface PdfPageEditorProps {
  visible: boolean;
  onClose: () => void;
  pdfUri: string;        // local or remote URI of the source PDF
  pdfTitle: string;       // display name
  outputDir: string;      // directory to save output PDFs
  onSaved?: (newUri: string, title: string) => void; // callback when a new PDF is saved
}

export default function PdfPageEditor({ visible, onClose, pdfUri, pdfTitle, outputDir, onSaved }: PdfPageEditorProps) {
  const [loading, setLoading] = useState(true);
  const [pageCount, setPageCount] = useState(0);
  const [selectedPages, setSelectedPages] = useState<number[]>([]);  // 1-indexed, in order
  const [saving, setSaving] = useState(false);
  const [mode, setMode] = useState<'select' | 'reorder'>('select');

  useEffect(() => {
    if (visible && pdfUri) {
      loadPdfInfo();
    }
    return () => {
      setSelectedPages([]);
      setMode('select');
      setLoading(true);
    };
  }, [visible, pdfUri]);

  const loadPdfInfo = async () => {
    setLoading(true);
    try {
      const info = await getPdfPageInfo(pdfUri);
      setPageCount(info.pageCount);
      // Select all pages by default
      setSelectedPages(Array.from({ length: info.pageCount }, (_, i) => i + 1));
    } catch (err: any) {
      Alert.alert('Error', `Could not load PDF: ${err.message}`);
      onClose();
    } finally {
      setLoading(false);
    }
  };

  const togglePage = (pageNum: number) => {
    setSelectedPages(prev => {
      if (prev.includes(pageNum)) {
        return prev.filter(p => p !== pageNum);
      } else {
        // Insert in sorted order for select mode
        const newArr = [...prev, pageNum].sort((a, b) => a - b);
        return newArr;
      }
    });
  };

  const selectAll = () => {
    setSelectedPages(Array.from({ length: pageCount }, (_, i) => i + 1));
  };

  const deselectAll = () => {
    setSelectedPages([]);
  };

  const selectRange = (start: number, end: number) => {
    const range: number[] = [];
    for (let i = start; i <= end; i++) range.push(i);
    setSelectedPages(range);
  };

  const movePageInOrder = (fromIdx: number, direction: 'up' | 'down') => {
    const toIdx = direction === 'up' ? fromIdx - 1 : fromIdx + 1;
    if (toIdx < 0 || toIdx >= selectedPages.length) return;
    setSelectedPages(prev => {
      const arr = [...prev];
      const temp = arr[fromIdx];
      arr[fromIdx] = arr[toIdx];
      arr[toIdx] = temp;
      return arr;
    });
  };

  const handleSave = async () => {
    if (selectedPages.length === 0) {
      Alert.alert('No Pages', 'Please select at least one page.');
      return;
    }

    setSaving(true);
    try {
      const baseName = pdfTitle.replace(/\.pdf$/i, '').replace(/[^a-zA-Z0-9._-]/g, '_');
      const suffix = selectedPages.length === pageCount ? 'reordered' : `pages_${selectedPages.join('-')}`;
      const outputName = `${baseName}_${suffix}_${Date.now()}.pdf`;
      const outputPath = `${outputDir}${outputName}`;

      await extractPages(pdfUri, selectedPages, outputPath);

      if (Platform.OS === 'android') {
        ToastAndroid.show(`✅ Saved: ${outputName}`, ToastAndroid.LONG);
      }

      if (onSaved) {
        onSaved(outputPath, outputName);
      }

      // Offer to share
      try {
        await Sharing.shareAsync(outputPath, {
          mimeType: 'application/pdf',
          UTI: 'com.adobe.pdf',
          dialogTitle: `Share ${outputName}`,
        });
      } catch {}

      onClose();
    } catch (err: any) {
      Alert.alert('Save Error', err.message || 'Failed to save PDF');
    } finally {
      setSaving(false);
    }
  };

  const renderPageGrid = () => {
    const pages: React.ReactNode[] = [];
    const cols = 5;
    for (let i = 1; i <= pageCount; i++) {
      const isSelected = selectedPages.includes(i);
      const orderIdx = selectedPages.indexOf(i);
      pages.push(
        <TouchableOpacity
          key={i}
          onPress={() => togglePage(i)}
          style={[
            s.pageCell,
            isSelected && s.pageCellSelected,
          ]}
          activeOpacity={0.6}
        >
          <Text style={[s.pageNum, isSelected && s.pageNumSelected]}>{i}</Text>
          {isSelected && (
            <View style={s.checkBadge}>
              <Text style={s.checkBadgeText}>{orderIdx + 1}</Text>
            </View>
          )}
        </TouchableOpacity>
      );
    }
    return pages;
  };

  const renderReorderList = () => {
    return selectedPages.map((pageNum, idx) => (
      <View key={`${pageNum}_${idx}`} style={s.reorderRow}>
        <View style={s.reorderBadge}>
          <Text style={s.reorderBadgeText}>{idx + 1}</Text>
        </View>
        <Text style={s.reorderPageLabel}>Page {pageNum}</Text>
        <View style={{ flex: 1 }} />
        <TouchableOpacity
          onPress={() => movePageInOrder(idx, 'up')}
          style={[s.reorderBtn, idx === 0 && { opacity: 0.3 }]}
          disabled={idx === 0}
        >
          <IconSymbol name="chevron.up" size={16} color="#FFF" />
        </TouchableOpacity>
        <TouchableOpacity
          onPress={() => movePageInOrder(idx, 'down')}
          style={[s.reorderBtn, idx === selectedPages.length - 1 && { opacity: 0.3 }]}
          disabled={idx === selectedPages.length - 1}
        >
          <IconSymbol name="chevron.down" size={16} color="#FFF" />
        </TouchableOpacity>
        <TouchableOpacity
          onPress={() => setSelectedPages(prev => prev.filter((_, i) => i !== idx))}
          style={[s.reorderBtn, { backgroundColor: '#EF444433' }]}
        >
          <IconSymbol name="xmark" size={14} color="#EF4444" />
        </TouchableOpacity>
      </View>
    ));
  };

  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose}>
      <View style={s.overlay}>
        <View style={s.container}>
          {/* Header */}
          <View style={s.header}>
            <View style={{ flex: 1 }}>
              <Text style={s.headerTitle}>📄 PDF Page Editor</Text>
              <Text style={s.headerSubtitle} numberOfLines={1}>
                {pdfTitle} {pageCount > 0 ? `• ${pageCount} pages` : ''}
              </Text>
            </View>
            <TouchableOpacity onPress={onClose} style={s.closeBtn}>
              <IconSymbol name="xmark" size={18} color="#FFF" />
            </TouchableOpacity>
          </View>

          {loading ? (
            <View style={s.loadingContainer}>
              <ActivityIndicator size="large" color="#4A62EB" />
              <Text style={s.loadingText}>Loading PDF...</Text>
            </View>
          ) : (
            <>
              {/* Mode Tabs */}
              <View style={s.tabRow}>
                <TouchableOpacity
                  onPress={() => setMode('select')}
                  style={[s.tab, mode === 'select' && s.tabActive]}
                >
                  <Text style={[s.tabText, mode === 'select' && s.tabTextActive]}>Select Pages</Text>
                </TouchableOpacity>
                <TouchableOpacity
                  onPress={() => setMode('reorder')}
                  style={[s.tab, mode === 'reorder' && s.tabActive]}
                >
                  <Text style={[s.tabText, mode === 'reorder' && s.tabTextActive]}>
                    Reorder ({selectedPages.length})
                  </Text>
                </TouchableOpacity>
              </View>

              {/* Quick Actions */}
              {mode === 'select' && (
                <View style={s.quickActions}>
                  <TouchableOpacity onPress={selectAll} style={s.quickBtn}>
                    <Text style={s.quickBtnText}>Select All</Text>
                  </TouchableOpacity>
                  <TouchableOpacity onPress={deselectAll} style={s.quickBtn}>
                    <Text style={s.quickBtnText}>Clear</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={() => {
                      Alert.prompt
                        ? Alert.prompt('Page Range', 'Enter range (e.g. 1-5)', (text) => {
                            const [s, e] = text.split('-').map(Number);
                            if (s && e && s <= e && s >= 1 && e <= pageCount) selectRange(s, e);
                          })
                        : (() => {
                            // Android doesn't have Alert.prompt, use simple toggle
                            const half = Math.ceil(pageCount / 2);
                            selectRange(1, half);
                          })();
                    }}
                    style={s.quickBtn}
                  >
                    <Text style={s.quickBtnText}>First Half</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={() => {
                      const half = Math.ceil(pageCount / 2) + 1;
                      selectRange(half, pageCount);
                    }}
                    style={s.quickBtn}
                  >
                    <Text style={s.quickBtnText}>Second Half</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={() => {
                      // Select odd pages
                      const odd: number[] = [];
                      for (let i = 1; i <= pageCount; i += 2) odd.push(i);
                      setSelectedPages(odd);
                    }}
                    style={s.quickBtn}
                  >
                    <Text style={s.quickBtnText}>Odd</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={() => {
                      // Select even pages
                      const even: number[] = [];
                      for (let i = 2; i <= pageCount; i += 2) even.push(i);
                      setSelectedPages(even);
                    }}
                    style={s.quickBtn}
                  >
                    <Text style={s.quickBtnText}>Even</Text>
                  </TouchableOpacity>
                </View>
              )}

              {/* Content Area */}
              <ScrollView style={s.scrollArea} showsVerticalScrollIndicator={false}>
                {mode === 'select' ? (
                  <View style={s.pageGrid}>
                    {renderPageGrid()}
                  </View>
                ) : (
                  <View style={s.reorderContainer}>
                    {selectedPages.length === 0 ? (
                      <Text style={s.emptyText}>No pages selected. Go to "Select Pages" to pick pages.</Text>
                    ) : (
                      renderReorderList()
                    )}
                  </View>
                )}
              </ScrollView>

              {/* Footer */}
              <View style={s.footer}>
                <Text style={s.footerInfo}>
                  {selectedPages.length} of {pageCount} pages selected
                </Text>
                <View style={s.footerButtons}>
                  <TouchableOpacity onPress={onClose} style={s.cancelBtn}>
                    <Text style={s.cancelBtnText}>Cancel</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={handleSave}
                    style={[s.saveBtn, selectedPages.length === 0 && { opacity: 0.4 }]}
                    disabled={selectedPages.length === 0 || saving}
                  >
                    {saving ? (
                      <ActivityIndicator size="small" color="#FFF" />
                    ) : (
                      <>
                        <IconSymbol name="square.and.arrow.down" size={16} color="#FFF" />
                        <Text style={s.saveBtnText}>Save as New PDF</Text>
                      </>
                    )}
                  </TouchableOpacity>
                </View>
              </View>
            </>
          )}
        </View>
      </View>
    </Modal>
  );
}

const s = StyleSheet.create({
  overlay: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.7)',
    justifyContent: 'flex-end',
  },
  container: {
    backgroundColor: '#14181F',
    borderTopLeftRadius: 24,
    borderTopRightRadius: 24,
    maxHeight: '85%',
    minHeight: '60%',
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    padding: 20,
    paddingBottom: 12,
    borderBottomWidth: 1,
    borderBottomColor: '#1E2330',
  },
  headerTitle: {
    color: '#FFF',
    fontSize: 18,
    fontWeight: '800',
  },
  headerSubtitle: {
    color: '#8A8F98',
    fontSize: 12,
    marginTop: 2,
  },
  closeBtn: {
    backgroundColor: '#2A2F3A',
    width: 36,
    height: 36,
    borderRadius: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  loadingContainer: {
    alignItems: 'center',
    justifyContent: 'center',
    padding: 60,
  },
  loadingText: {
    color: '#8A8F98',
    fontSize: 13,
    marginTop: 12,
  },
  tabRow: {
    flexDirection: 'row',
    paddingHorizontal: 16,
    paddingTop: 12,
    gap: 8,
  },
  tab: {
    flex: 1,
    paddingVertical: 10,
    borderRadius: 12,
    backgroundColor: '#1C202B',
    alignItems: 'center',
  },
  tabActive: {
    backgroundColor: '#4A62EB',
  },
  tabText: {
    color: '#8A8F98',
    fontSize: 13,
    fontWeight: '600',
  },
  tabTextActive: {
    color: '#FFF',
  },
  quickActions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    paddingHorizontal: 16,
    paddingTop: 10,
    gap: 6,
  },
  quickBtn: {
    backgroundColor: '#1C202B',
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  quickBtnText: {
    color: '#8A8F98',
    fontSize: 11,
    fontWeight: '600',
  },
  scrollArea: {
    flex: 1,
    paddingHorizontal: 16,
    paddingTop: 12,
  },
  pageGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    paddingBottom: 20,
  },
  pageCell: {
    width: 54,
    height: 54,
    borderRadius: 12,
    backgroundColor: '#1C202B',
    borderWidth: 1.5,
    borderColor: '#2A2F3A',
    alignItems: 'center',
    justifyContent: 'center',
  },
  pageCellSelected: {
    backgroundColor: '#4A62EB22',
    borderColor: '#4A62EB',
  },
  pageNum: {
    color: '#8A8F98',
    fontSize: 16,
    fontWeight: '700',
  },
  pageNumSelected: {
    color: '#4A62EB',
  },
  checkBadge: {
    position: 'absolute',
    top: -4,
    right: -4,
    backgroundColor: '#4A62EB',
    width: 18,
    height: 18,
    borderRadius: 9,
    alignItems: 'center',
    justifyContent: 'center',
  },
  checkBadgeText: {
    color: '#FFF',
    fontSize: 9,
    fontWeight: '800',
  },
  reorderContainer: {
    paddingBottom: 20,
    gap: 6,
  },
  reorderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#1C202B',
    borderRadius: 12,
    padding: 12,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  reorderBadge: {
    width: 28,
    height: 28,
    borderRadius: 14,
    backgroundColor: '#4A62EB',
    alignItems: 'center',
    justifyContent: 'center',
    marginRight: 12,
  },
  reorderBadgeText: {
    color: '#FFF',
    fontSize: 12,
    fontWeight: '800',
  },
  reorderPageLabel: {
    color: '#FFF',
    fontSize: 14,
    fontWeight: '600',
  },
  reorderBtn: {
    width: 32,
    height: 32,
    borderRadius: 8,
    backgroundColor: '#2A2F3A',
    alignItems: 'center',
    justifyContent: 'center',
    marginLeft: 6,
  },
  emptyText: {
    color: '#4C5361',
    fontSize: 13,
    fontStyle: 'italic',
    textAlign: 'center',
    padding: 40,
  },
  footer: {
    padding: 16,
    borderTopWidth: 1,
    borderTopColor: '#1E2330',
  },
  footerInfo: {
    color: '#8A8F98',
    fontSize: 12,
    textAlign: 'center',
    marginBottom: 10,
  },
  footerButtons: {
    flexDirection: 'row',
    gap: 10,
  },
  cancelBtn: {
    flex: 1,
    paddingVertical: 14,
    borderRadius: 14,
    backgroundColor: '#2A2F3A',
    alignItems: 'center',
  },
  cancelBtnText: {
    color: '#FFF',
    fontSize: 14,
    fontWeight: '700',
  },
  saveBtn: {
    flex: 2,
    paddingVertical: 14,
    borderRadius: 14,
    backgroundColor: '#4A62EB',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
  },
  saveBtnText: {
    color: '#FFF',
    fontSize: 14,
    fontWeight: '700',
  },
});
