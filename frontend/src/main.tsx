import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { applicaVesteAlDocumento } from "./experience";
import "./index.css";
import "./nastro.css";

// Prima del primo fotogramma: applicarla dentro React farebbe vedere un lampo di veste classica
// a chi ha scelto Nastro, ogni volta che apre la pagina.
applicaVesteAlDocumento();

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
