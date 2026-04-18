import React, { useState, useEffect } from 'react';
import { StyleSheet, Text, View, TouchableOpacity, SafeAreaView, ActivityIndicator, Dimensions, TextInput, KeyboardAvoidingView, Platform } from 'react-native';
import * as MediaLibrary from 'expo-media-library';
import * as FileSystem from 'expo-file-system';
import DateTimePicker from '@react-native-community/datetimepicker';

const { width } = Dimensions.get('window');

export default function App() {
  const [hasPermission, setHasPermission] = useState(null);
  
  // Date State
  const [startDate, setStartDate] = useState(new Date(Date.now() - 30 * 24 * 60 * 60 * 1000)); // Default 1 month ago
  const [endDate, setEndDate] = useState(new Date());
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);
  
  // Scanner State
  const [mediaAssets, setMediaAssets] = useState([]);
  const [isScanning, setIsScanning] = useState(false);
  
  // Upload State
  const [isUploading, setIsUploading] = useState(false);
  const [uploadIndex, setUploadIndex] = useState(0);
  const [serverIp, setServerIp] = useState("192.168.1.5:3000");

  useEffect(() => {
    (async () => {
      const { status } = await MediaLibrary.requestPermissionsAsync();
      setHasPermission(status === 'granted');
    })();
  }, []);

  const scanMedia = async () => {
    if (!hasPermission) return;
    setIsScanning(true);
    setMediaAssets([]);
    
    try {
      let allFound = [];
      let hasNextPage = true;
      let after = undefined;

      while (hasNextPage) {
        let media = await MediaLibrary.getAssetsAsync({
          first: 100,
          after: after,
          mediaType: ['photo', 'video'],
          createdAfter: startDate.getTime(),
          createdBefore: endDate.getTime(),
          sortBy: [[MediaLibrary.SortBy.creationTime, false]]
        });

        allFound = [...allFound, ...media.assets];
        hasNextPage = media.hasNextPage;
        after = media.endCursor;
      }

      setMediaAssets(allFound);
    } catch (e) {
      console.error(e);
    }
    setIsScanning(false);
  };

  const uploadToPc = async () => {
    if (mediaAssets.length === 0 || !serverIp) return;
    setIsUploading(true);
    setUploadIndex(0);

    const uploadUrl = `http://${serverIp}/api/archive_upload`;

    for(let i = 0; i < mediaAssets.length; i++) {
        setUploadIndex(i + 1);
        const asset = mediaAssets[i];
        
        try {
            // Get the physical file path
            const assetInfo = await MediaLibrary.getAssetInfoAsync(asset.id);
            const physicalPath = assetInfo.localUri || assetInfo.uri;

            await FileSystem.uploadAsync(uploadUrl, physicalPath, {
                fieldName: 'file',
                httpMethod: 'POST',
                uploadType: FileSystem.FileSystemUploadType.MULTIPART,
                headers: {
                    'X-Original-Date': asset.creationTime.toString()
                }
            });
        } catch (error) {
            console.error(`Failed to upload ${asset.filename}`, error);
        }
    }
    
    setIsUploading(false);
    setMediaAssets([]); 
  };

  if (hasPermission === false) {
    return (
      <View style={styles.container}>
        <Text style={styles.errorText}>No access to internal storage.</Text>
      </View>
    );
  }

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView behavior={Platform.OS === "ios" ? "padding" : "height"} style={{flex: 1}}>
      <View style={styles.header}>
        <Text style={styles.title}>AdvanceClip</Text>
        <Text style={styles.subtitle}>Archival Interface</Text>
        
        <View style={styles.ipContainer}>
           <Text style={styles.ipLabel}>PC SERVER IP</Text>
           <TextInput
             style={styles.ipInput}
             value={serverIp}
             onChangeText={setServerIp}
             placeholder="192.168.1.X:3000"
             placeholderTextColor="#4C5361"
             keyboardType="numbers-and-punctuation"
           />
        </View>
      </View>

      <View style={styles.card}>
        <Text style={styles.sectionHeader}>Target Dates</Text>
        
        <View style={styles.dateRow}>
          <TouchableOpacity style={styles.dateButton} onPress={() => setShowStartPicker(true)}>
            <Text style={styles.dateLabel}>Start</Text>
            <Text style={styles.dateValue}>{startDate.toLocaleDateString()}</Text>
          </TouchableOpacity>

          <TouchableOpacity style={styles.dateButton} onPress={() => setShowEndPicker(true)}>
            <Text style={styles.dateLabel}>End</Text>
            <Text style={styles.dateValue}>{endDate.toLocaleDateString()}</Text>
          </TouchableOpacity>
        </View>

        {showStartPicker && (
          <DateTimePicker
            value={startDate}
            mode="date"
            display="default"
            onChange={(e, date) => { setShowStartPicker(false); if (date) setStartDate(date); }}
          />
        )}
        {showEndPicker && (
          <DateTimePicker
            value={endDate}
            mode="date"
            display="default"
            onChange={(e, date) => { setShowEndPicker(false); if (date) setEndDate(date); }}
          />
        )}
      </View>

      <View style={styles.actionContainer}>
        <TouchableOpacity 
          style={[styles.primaryButton, isScanning && styles.buttonDisabled]} 
          onPress={scanMedia}
          disabled={isScanning || isUploading}>
          {isScanning ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Scan Device Gallery</Text>}
        </TouchableOpacity>

        {mediaAssets.length > 0 && !isScanning && (
          <View style={styles.statsPanel}>
             <Text style={styles.statsValue}>{mediaAssets.length}</Text>
             <Text style={styles.statsLabel}>High-Quality items discovered</Text>
          </View>
        )}
      </View>

      {mediaAssets.length > 0 && (
         <View style={styles.footerLayer}>
            <TouchableOpacity 
              style={[styles.extractButton, isUploading && styles.extractProgressButton]} 
              onPress={uploadToPc}
              disabled={isUploading}>
              <Text style={styles.extractButtonText}>
                  {isUploading ? `Extracting... ${uploadIndex} / ${mediaAssets.length}` : 'Start Network Extraction'}
              </Text>
              {isUploading && (
                  <View style={[styles.progressBar, { width: `${(uploadIndex/mediaAssets.length)*100}%` }]} />
              )}
            </TouchableOpacity>
         </View>
      )}
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#0F1115', 
  },
  header: {
    paddingTop: 50,
    paddingHorizontal: 24,
    marginBottom: 20,
  },
  title: {
    fontSize: 34,
    fontWeight: '800',
    color: '#FFFFFF',
    letterSpacing: -0.5,
  },
  subtitle: {
    fontSize: 16,
    color: '#8A8F98',
    marginTop: 4,
    fontWeight: '500',
    textTransform: 'uppercase',
    letterSpacing: 1.5,
    marginBottom: 20,
  },
  ipContainer: {
    backgroundColor: '#1C1F26',
    borderRadius: 12,
    paddingHorizontal: 15,
    paddingVertical: 12,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  ipLabel: {
    color: '#4A62EB',
    fontSize: 10,
    fontWeight: '700',
    marginBottom: 4,
    letterSpacing: 1,
  },
  ipInput: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '600',
  },
  card: {
    backgroundColor: '#1C1F26',
    marginHorizontal: 20,
    borderRadius: 24,
    padding: 24,
    borderWidth: 1,
    borderColor: '#2A2F3A',
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.3,
    shadowRadius: 20,
  },
  sectionHeader: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '600',
    marginBottom: 20,
  },
  dateRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
  },
  dateButton: {
    backgroundColor: '#252A34',
    padding: 16,
    borderRadius: 16,
    width: '47%',
  },
  dateLabel: {
    color: '#8A8F98',
    fontSize: 12,
    fontWeight: '600',
    textTransform: 'uppercase',
    marginBottom: 6,
  },
  dateValue: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '500',
  },
  actionContainer: {
    padding: 20,
    marginTop: 10,
    alignItems: 'center',
  },
  primaryButton: {
    backgroundColor: '#4A62EB',
    width: '100%',
    paddingVertical: 18,
    borderRadius: 16,
    alignItems: 'center',
    shadowColor: '#4A62EB',
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.4,
    shadowRadius: 15,
  },
  buttonDisabled: {
    opacity: 0.7,
  },
  buttonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    letterSpacing: 0.5,
  },
  statsPanel: {
    marginTop: 40,
    alignItems: 'center',
  },
  statsValue: {
    fontSize: 48,
    fontWeight: '800',
    color: '#22D3EE', 
  },
  statsLabel: {
    color: '#8A8F98',
    fontSize: 14,
    fontWeight: '500',
    marginTop: 5,
  },
  footerLayer: {
    position: 'absolute',
    bottom: 40,
    left: 20,
    right: 20,
  },
  extractButton: {
    backgroundColor: '#10B981', // Emerald green
    width: '100%',
    paddingVertical: 20,
    borderRadius: 20,
    alignItems: 'center',
    overflow: 'hidden',
  },
  extractProgressButton: {
    backgroundColor: '#1E293B',
  },
  extractButtonText: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: 'bold',
    zIndex: 10,
  },
  progressBar: {
    position: 'absolute',
    left: 0,
    top: 0,
    bottom: 0,
    backgroundColor: '#10B981',
    opacity: 0.3,
    zIndex: 1,
  },
  errorText: {
    color: '#FF4A4A',
    fontSize: 16,
    textAlign: 'center',
    marginTop: 100,
  }
});
