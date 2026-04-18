import { initializeApp } from "firebase/app";
import { getDatabase } from "firebase/database";
import { getStorage } from "firebase/storage";

const firebaseConfig = {
  apiKey: "AIzaSyA52ZXmxx1auJshsv-uuayQRHD22D7zdwk",
  authDomain: "advance-sync.firebaseapp.com",
  projectId: "advance-sync",
  storageBucket: "advance-sync.firebasestorage.app",
  messagingSenderId: "49241495533",
  appId: "1:49241495533:web:a774fec697271c1b81f9e4",
  measurementId: "G-FHVL9ESM85",
  databaseURL: "https://advance-sync-default-rtdb.firebaseio.com"
};

const app = initializeApp(firebaseConfig);
export const database = getDatabase(app);
export const storage = getStorage(app);
export default app;
