import { mount } from 'svelte';
import App from './App.svelte';
import '@dragonglass/instruments/theme/tokens.css';

mount(App, { target: document.getElementById('app')! });
